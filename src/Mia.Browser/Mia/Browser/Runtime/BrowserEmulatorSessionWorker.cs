// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Threading.Channels;
using AvrCore.Execution;
using Mia.Emulator.Modem;

namespace Mia.Browser.Runtime;

/// <summary>
/// Owns the complete emulated machine on the browser runtime's sole bounded
/// ThreadPool worker. The Avalonia thread communicates only through this
/// command channel and immutable event snapshots.
/// </summary>
internal sealed class BrowserEmulatorSessionWorker
{
    static readonly long BatchTargetTicks = Stopwatch.Frequency * 6 / 1_000;
    static readonly TimeSpan MaximumCatchUpDebt = TimeSpan.FromMilliseconds(250);
    const int MinimumBatchSize = 128;
    const int MaximumBatchSize = 65_536;
    const int InitialBatchSize = 4_096;

    readonly BrowserEmulatorSession _owner;
    readonly Cpu _cpu;
    readonly ArmModem _modem;
    readonly MiaPersistenceSnapshot _persistenceSnapshot;
    readonly IMiaPersistenceStore? _persistenceStore;
    readonly string? _persistenceKey;
    readonly bool _paceToRealTime;
    readonly bool _diagnostics;
    readonly Channel<Action<MiaMachine>> _commands =
        Channel.CreateUnbounded<Action<MiaMachine>>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly RealTimePacer _pacer = new(maximumCatchUpDebt: MaximumCatchUpDebt);
    BrowserMediaClock? _mediaClock;
    int _startedState;
    int _stopping;
    int _running;

    public BrowserEmulatorSessionWorker(
        BrowserEmulatorSession owner,
        Cpu cpu,
        ArmModem modem,
        MiaPersistenceSnapshot persistenceSnapshot,
        IMiaPersistenceStore? persistenceStore,
        string? persistenceKey,
        bool paceToRealTime,
        bool diagnostics)
    {
        _owner = owner;
        _cpu = cpu;
        _modem = modem;
        _persistenceSnapshot = persistenceSnapshot;
        _persistenceStore = persistenceStore;
        _persistenceKey = persistenceKey;
        _paceToRealTime = paceToRealTime;
        _diagnostics = diagnostics;
    }

    public Task Started => _started.Task;

    public Task Completion => _completion.Task;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public void Start()
    {
        if (Interlocked.Exchange(ref _startedState, 1) != 0)
        {
            throw new InvalidOperationException(
                "The browser emulator worker was already started.");
        }
        // Threaded builds lease the runtime's registered ThreadPool worker.
        // Cooperative builds use the same async loop on the browser thread.
#if BROWSER_THREADS
        _ = Task.Run(RunAsync);
#else
        _ = RunAsync();
#endif
    }

    public bool Post(Action<MiaMachine> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _commands.Writer.TryWrite(command);
    }

    public bool TryInvoke<TResult>(
        Func<MiaMachine, TResult> function,
        out TResult value)
    {
        ArgumentNullException.ThrowIfNull(function);
        var completion = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted = Post(machine => CompleteInvocation(
            machine,
            function,
            completion));
        value = posted
            ? completion.Task.GetAwaiter().GetResult()
            : default!;
        return posted;
    }

    static void CompleteInvocation<TResult>(
        MiaMachine machine,
        Func<MiaMachine, TResult> function,
        TaskCompletionSource<TResult> completion)
    {
        try
        {
            completion.TrySetResult(function(machine));
        }
        catch (Exception error) when (completion.TrySetException(error))
        {
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }
        _commands.Writer.TryComplete();
    }

    async Task RunAsync()
    {
        try
        {
            await RunOwnedMachineAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (ReportFailureAndStop(error))
        {
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            _started.TrySetException(new InvalidOperationException(
                "The emulator worker stopped during startup."));
            _completion.TrySetResult();
        }
    }

    async Task RunOwnedMachineAsync()
    {
        using MiaMachine machine = CreateMachine();
        MiaPersistenceCoordinator? persistenceCoordinator = null;
        try
        {
            ValidateInlineTopology(machine);

            if (_paceToRealTime && BrowserAudioRing.TryReadClock(out uint read, out _))
                _mediaClock = new(read, _owner.RaiseFrameReady);

            ConfigureMachine(machine);
            persistenceCoordinator = CreatePersistenceCoordinator(machine);

            using var audio = _paceToRealTime
                ? new AsicTonePcmWorker(_owner.RaiseAudioReady, machine.Cycles)
                : null;
            if (audio is not null)
            {
                audio.Faulted += error =>
                {
                    _owner.ReportWorkerFailure(error);
                    Stop();
                };
                machine.ToneGenerator.StateChanged += audio.Enqueue;
                audio.Enqueue(machine.ToneGenerator.CurrentState);
            }

            _pacer.Reanchor(machine.Cycles);
            Volatile.Write(ref _running, 1);
            _owner.RaiseStatusChanged("Booting · power key held");
            _started.TrySetResult();
            await RunLoopAsync(
                machine,
                persistenceCoordinator,
                audio).ConfigureAwait(false);
        }
        finally
        {
            await FlushPersistenceAsync(
                machine,
                persistenceCoordinator).ConfigureAwait(false);
        }
    }

    MiaMachine CreateMachine() =>
        new(
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            Convert.FromHexString("321A065432100654"),
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            persistenceSnapshot: _persistenceSnapshot,
            liveGsm: new MiaLiveGsmOptions(),
            dedicatedWorkers: false,
            preparedCpu: _cpu,
            preparedModem: _modem);

    void ConfigureMachine(MiaMachine machine)
    {
        var liveGsm = machine.LiveGsm ?? throw new InvalidOperationException(
            "Could not create the live GSM controller.");
        machine.BluetoothEmulationStatusChanged +=
            _owner.RaiseBluetoothStatusChanged;
        machine.InfraredObjectPeer!.StatusChanged +=
            _owner.RaiseInfraredStatusChanged;
        _owner.ApplyBluetoothConfiguration(machine);
        _owner.ApplyStagedInfraredObject(machine);
        liveGsm.MessageEmitted += message =>
        {
            if (_diagnostics)
            {
                Console.WriteLine($"Mia browser GSM: {message}");
            }
            _owner.RaiseGsmMessageEmitted(message);
        };
        machine.FramePublished += frame =>
        {
            if (_mediaClock is null) _owner.RaiseFrameReady(frame);
            else _mediaClock.Enqueue(machine.Cycles, frame);
        };
        machine.StatusIndicators.StateChanged +=
            _owner.RaiseStatusLedsChanged;
        machine.PhoneLeds.StateChanged +=
            _owner.RaisePhoneLedsChanged;
        _owner.RaiseBluetoothStatusChanged(
            machine.GetBluetoothEmulationStatus());
        _owner.RaiseInfraredStatusChanged(
            machine.GetInfraredTransferStatus());
        _owner.RaiseStatusLedsChanged(machine.StatusIndicators.State);
        _owner.RaisePhoneLedsChanged(machine.PhoneLeds.State);
    }

    MiaPersistenceCoordinator? CreatePersistenceCoordinator(
        MiaMachine machine)
    {
        if (_persistenceStore is null || _persistenceKey is null)
        {
            return null;
        }
        var coordinator = new MiaPersistenceCoordinator(new(
            _persistenceKey,
            _persistenceStore,
            _persistenceSnapshot));
        coordinator.SnapshotSaved += snapshot =>
            Console.WriteLine(
                $"Mia browser persistence: saved " +
                $"{snapshot.SimFiles.Length:n0} changed SIM file(s)");
        coordinator.SaveFailed += error =>
            Console.Error.WriteLine(
                $"Mia browser persistence save failed: {error.Message}");
        coordinator.MarkLoaded(machine);
        machine.PersistenceChanged += _ => coordinator.ScheduleSave(machine);
        Console.WriteLine(
            $"Mia browser persistence: key={_persistenceKey} " +
            $"simFiles={_persistenceSnapshot.SimFiles.Length}");
        return coordinator;
    }

    async Task RunLoopAsync(
        MiaMachine machine,
        MiaPersistenceCoordinator? persistenceCoordinator,
        AsicTonePcmWorker? audio)
    {
        var batchSize = InitialBatchSize;
        DrainCommands(machine);
        while (Volatile.Read(ref _stopping) == 0 && !machine.IsStopped)
        {
            var started = Stopwatch.GetTimestamp();
            int completed = machine.RunInteractiveWorkItems(batchSize);
            long elapsed = Math.Max(1, Stopwatch.GetTimestamp() - started);
            batchSize = AdaptBatchSize(batchSize, completed, elapsed);

            audio?.AdvanceTo(machine.Cycles);
            await WaitUntilNextGrantAsync(machine).ConfigureAwait(false);
            DrainCommands(machine);
        }
        DrainCommands(machine);
        PublishMachineStop(machine, persistenceCoordinator);
    }

    void PublishMachineStop(
        MiaMachine machine,
        MiaPersistenceCoordinator? persistenceCoordinator)
    {
        if (machine.IsStopped)
        {
            persistenceCoordinator?.ScheduleSave(machine, force: true);
            _owner.RaiseStatusChanged($"Stopped · {machine.StopReason}");
        }
    }

    void DrainCommands(MiaMachine machine)
    {
        while (_commands.Reader.TryRead(out Action<MiaMachine>? command))
        {
            try
            {
                command(machine);
            }
            catch (Exception error) when (
                _owner.ReportWorkerCommandFailure(error))
            {
            }
        }
    }

    async Task WaitUntilNextGrantAsync(MiaMachine machine)
    {
        while (_mediaClock is not null && BrowserAudioRing.TryReadClock(out uint read, out _))
        {
            // The device's consumed sample count is the presentation clock
            // for both media. Refill its reserve before delaying the core;
            // an unrelated wall-clock reanchor must not starve playback.
            _mediaClock.Advance(read);
            int milliseconds = _mediaClock.GetDelayMilliseconds(machine.Cycles);
            if (milliseconds == 0)
            {
                await Task.Yield();
                return;
            }
            await Task.Delay(milliseconds).ConfigureAwait(false);
            DrainCommands(machine);
            if (Volatile.Read(ref _stopping) != 0 || machine.IsStopped) return;
        }
        if (_mediaClock is not null)
        {
            _mediaClock = null;
            _pacer.Reanchor(machine.Cycles);
            _owner.RaiseFrameReady(machine.Frame);
        }
        if (!_paceToRealTime)
        {
            await Task.Yield();
            return;
        }

        TimeSpan delay = _pacer.GetDelay(machine.Cycles);
        if (delay <= TimeSpan.Zero)
        {
            await Task.Yield();
            return;
        }

        int delayMilliseconds = Math.Clamp(
            (int)Math.Ceiling(delay.TotalMilliseconds),
            1,
            6);
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
    }

    async Task FlushPersistenceAsync(
        MiaMachine machine,
        MiaPersistenceCoordinator? persistenceCoordinator)
    {
        if (persistenceCoordinator is null)
        {
            return;
        }

        try
        {
            await persistenceCoordinator.FlushAsync(machine)
                .ConfigureAwait(false);
        }
        catch (IOException error)
        {
            _owner.RaiseStatusChanged(
                $"Persistence save failed · {error.Message}");
            await Console.Error.WriteLineAsync(
                $"Mia browser persistence flush failed: {error}")
                .ConfigureAwait(false);
        }
    }

    bool ReportFailureAndStop(Exception error)
    {
        _started.TrySetException(error);
        _owner.ReportWorkerFailure(error);
        return true;
    }

    void ValidateInlineTopology(MiaMachine machine)
    {
        foreach (MiaWorker worker in machine.Workers)
        {
            if (worker.HasDedicatedThread || !worker.IsCurrentThread)
            {
                throw new InvalidOperationException(
                    $"Browser event target '{worker.Name}' does not run inline " +
                    "on the browser session worker.");
            }
        }
        if (_diagnostics)
        {
            Console.WriteLine(
                $"Mia browser workers: emulator runtime worker " +
                $"{Environment.CurrentManagedThreadId}, " +
                $"{machine.Workers.Count:n0} inline logical event targets");
        }
    }

    static int AdaptBatchSize(int current, int completed, long elapsedTicks)
    {
        if (completed <= 0)
        {
            return MinimumBatchSize;
        }
        long desired = (long)completed * BatchTargetTicks / elapsedTicks;
        long bounded = Math.Clamp(
            desired,
            Math.Max(MinimumBatchSize, current / 2),
            Math.Min(MaximumBatchSize, current * 2));
        return (int)Math.Clamp(
            (current * 3L + bounded) / 4,
            MinimumBatchSize,
            MaximumBatchSize);
    }
}
