// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Machine;

/// <summary>
/// Explicit diagnostic boundary for synthetic CLI stimuli and read-only
/// reverse-engineering probes. Every mutation still enters the corresponding
/// device worker and firmware-visible IRQ/MMIO boundary.
/// </summary>
internal sealed class MiaMachineDiagnostics : IDisposable
{
    readonly MiaMachine _machine;
    readonly MiaWorker _avrWorker;
    readonly MiaWorker _inputWorker;
    readonly MiaWorker _simWorker;
    readonly MiaWorker _byteChannelWorker;
    readonly MiaWorker _interruptRouterWorker;
    bool _disposed;

    public MiaMachineDiagnostics(
        MiaMachine machine,
        MiaWorker avrWorker,
        MiaWorker inputWorker,
        MiaWorker simWorker,
        MiaWorker byteChannelWorker,
        MiaWorker interruptRouterWorker)
    {
        _machine = machine;
        _avrWorker = avrWorker;
        _inputWorker = inputWorker;
        _simWorker = simWorker;
        _byteChannelWorker = byteChannelWorker;
        _interruptRouterWorker = interruptRouterWorker;
    }

    public event Action<MiaDisplayTransactionObservation>?
        DisplayTransactionObserved;

    public IMiaExecutionObserver? ExecutionObserver
    {
        get => _machine.ExecutionObserver;
        set => _machine.ExecutionObserver = value;
    }

    /// <summary>
    /// Starts exact AVR PC and backward-branch counting without installing an
    /// execution observer, so verified idle acceleration stays enabled.
    /// </summary>
    public void StartExecutionHotspotProfiling() =>
        InvokeAvr(_machine.StartExecutionHotspotProfiling);

    public IReadOnlyList<MiaExecutionHotspot> SnapshotExecutionHotspots() =>
        InvokeAvr(_machine.SnapshotExecutionHotspots);

    /// <summary>
    /// Records why visits to the resolved idle-loop entry are rejected. This
    /// is opt-in and remains entirely within the AVR owner worker.
    /// </summary>
    public void EnableIdleFastForwardDiagnostics() =>
        InvokeAvr(_machine.EnableIdleFastForwardDiagnostics);

    /// <summary>
    /// Disables firmware shortcuts that access the AVR backing array without
    /// crossing the data bus. Complete data-bus tracing requires this first.
    /// </summary>
    internal void DisableFirmwareFastPaths() =>
        InvokeAvr(_machine.DisableFirmwareFastPathsForDiagnostics);

    public MiaIdleFastForwardDiagnostics
        SnapshotIdleFastForwardDiagnostics() =>
        InvokeAvr(_machine.SnapshotIdleFastForwardDiagnostics);

    public void SchedulePowerKeyAt(long cycle, bool pressed) =>
        ScheduleAt(
            _inputWorker,
            cycle,
            () => ApplyPowerKey(pressed));

    public void ScheduleKeyAt(
        long cycle,
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed,
        bool raiseInterrupt = true) =>
        ScheduleAt(
            _inputWorker,
            cycle,
            () => ApplyKey(new(
                scanMask,
                rowMask,
                secondaryScanMask,
                pressed,
                raiseInterrupt)));

    public void ScheduleSimReceiveAt(
        long cycle,
        ReadOnlyMemory<byte> bytes,
        Action? delivered = null)
    {
        var captured = bytes.ToArray();
        ScheduleAt(
            _simWorker,
            cycle,
            () =>
            {
                foreach (var value in captured)
                {
                    _machine.SimInterface.QueueReceivedByte(value);
                }
                delivered?.Invoke();
            });
    }

    public void ScheduleChannelReceiveAt(
        long cycle,
        int channel,
        ReadOnlyMemory<byte> bytes)
    {
        var captured = bytes.ToArray();
        ScheduleAt(
            _byteChannelWorker,
            cycle,
            () =>
            {
                foreach (var value in captured)
                {
                    _machine.ByteChannels.QueueReceivedByte(channel, value);
                }
            });
    }

    public void InstallDataReadProbe(
        int address,
        Action<MiaDataReadObservation> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        InvokeAvr(() =>
        {
            _machine.DisableFirmwareFastPathsForDiagnostics();
            ValidateDataRange(address, 1);
            var cpu = _machine.Cpu;
            var previousHook = cpu.ReadHooks[address];
            cpu.ReadHooks[address] = hookAddress =>
            {
                var value = previousHook?.Invoke(hookAddress) ??
                    cpu.Data[hookAddress];
                probe(new(
                    _machine.Clock.Cycles,
                    cpu.PC,
                    hookAddress,
                    value));
                return value;
            };
        });
    }

    public void InstallDataWriteProbe(
        int address,
        Action<MiaDataWriteObservation> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        InvokeAvr(() =>
        {
            ValidateDataRange(address, 1);
            var cpu = _machine.Cpu;
            var previousHook = cpu.WriteHooks[address];
            cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
            {
                probe(new(
                    _machine.Clock.Cycles,
                    cpu.PC,
                    hookAddress,
                    oldValue,
                    value,
                    mask));
                return previousHook?.Invoke(
                    value,
                    oldValue,
                    hookAddress,
                    mask) ?? false;
            };
        });
    }

    public TResult InspectAvr<TResult>(Func<Cpu, TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return InvokeAvr(() => inspect(_machine.Cpu));
    }

    public byte[] SnapshotData(int address, int length) =>
        InspectAvr(cpu =>
        {
            ValidateDataRange(address, length);
            return cpu.Data.AsSpan(address, length).ToArray();
        });

    public byte[] SnapshotProgram(int address, int length) =>
        InspectAvr(cpu =>
        {
            if (address < 0 || length < 0 ||
                (long)address + length > cpu.ProgBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(address));
            }
            return cpu.ProgBytes.AsSpan(address, length).ToArray();
        });

    public IReadOnlyDictionary<byte, AsicInterruptRoute>
        SnapshotInterruptRoutes() =>
        InvokeRouter(
            () => _machine.InterruptRouter.Routes.ToDictionary());

    public void SynchronizeAvrClock() =>
        InvokeAvr(() => _machine.Clock.SynchronizeAvrCycles(
            _machine.Cpu.Cycles));

    public void FlushHostEvents() => _machine.FlushHostEvents();

    internal void PublishDisplayTransaction(
        MiaDisplayTransactionObservation observation)
    {
        var observed = DisplayTransactionObserved;
        if (observed is not null)
        {
            _machine.PostHostEvent(() => observed(observation));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _machine.ExecutionObserver = null;
        DisplayTransactionObserved = null;
    }

    void ScheduleAt(MiaWorker worker, long cycle, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        InvokeAvr(() =>
        {
            if (cycle < _machine.Clock.Cycles)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cycle),
                    "A diagnostic event cannot be scheduled in the past.");
            }
            if (cycle == _machine.Clock.Cycles)
            {
                worker.Invoke(callback);
                return;
            }
            _machine.Clock.Schedule(
                worker,
                callback,
                cycle - _machine.Clock.Cycles);
        });
    }

    void ApplyPowerKey(bool pressed)
    {
        if (pressed)
        {
            _machine.Keypad.Press(
                AsicKeypad.NoPowerScanMask,
                AsicKeypad.NoPowerRowMask);
            _machine.PowerPorts.PressPower();
        }
        else
        {
            _machine.Keypad.Release(
                AsicKeypad.NoPowerScanMask,
                AsicKeypad.NoPowerRowMask);
            _machine.PowerPorts.ReleasePower();
        }
        _machine.InterruptController.RaiseHighPriority(
            AsicInterruptController.KeypadSource);
    }

    void ApplyKey(MiaScheduledKey key)
    {
        if (key.Pressed)
        {
            _machine.Keypad.Press(
                key.ScanMask,
                key.RowMask,
                key.SecondaryScanMask);
        }
        else
        {
            _machine.Keypad.Release(
                key.ScanMask,
                key.RowMask,
                key.SecondaryScanMask);
        }
        if (key.RaiseInterrupt)
        {
            _machine.InterruptController.RaiseHighPriority(
                AsicInterruptController.KeypadSource);
        }
    }

    void ValidateDataRange(int address, int length)
    {
        if (address < 0 || length < 0 ||
            (long)address + length > _machine.Cpu.Data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }
    }

    void InvokeAvr(Action action)
    {
        if (_avrWorker.IsCurrentThread)
        {
            action();
        }
        else
        {
            _avrWorker.Invoke(action);
        }
    }

    TResult InvokeAvr<TResult>(Func<TResult> function)
    {
        if (_avrWorker.IsCurrentThread)
        {
            return function();
        }
        return _avrWorker.Invoke(function);
    }

    TResult InvokeRouter<TResult>(Func<TResult> function)
    {
        if (_interruptRouterWorker.IsCurrentThread)
        {
            return function();
        }
        return _interruptRouterWorker.Invoke(function);
    }
}
