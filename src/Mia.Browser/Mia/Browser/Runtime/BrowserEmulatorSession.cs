// SPDX-License-Identifier: MIT

using AvrCore.Execution;
using Mia.Emulator.Modem;

namespace Mia.Browser.Runtime;

/// <summary>
/// Runs the engine on the browser worker and maps engine-owned values to the
/// host-neutral contracts consumed by <see cref="Mia.App"/>.
/// </summary>
internal sealed class BrowserEmulatorSession : IMainViewSession
{
    Cpu? _cpu;
    ArmModem? _modem;
    readonly bool _diagnostics;
    readonly bool _paceToRealTime;
    internal bool PaceToRealTime => _paceToRealTime;
    readonly IMiaPersistenceStore? _persistenceStore;
    readonly string? _persistenceKey;
    Task? _runTask;
    BrowserEmulatorSessionWorker? _worker;
    BrowserEmulatorSessionBluetoothConfiguration _bluetoothConfiguration = new(
        PeerEnabled: true,
        PeerPin: ArmModemBluetoothPeripheral.DefaultEmulatedPeerPin,
        IncomingObjectPushEnabled: true,
        StagedObject: null);
    MiaTransferObject? _stagedInfraredObject;
    int _runningNotified;

    public BrowserEmulatorSession(
        Cpu cpu,
        ArmModem modem,
        bool diagnostics = false,
        bool paceToRealTime = true,
        IMiaPersistenceStore? persistenceStore = null,
        string? persistenceKey = null)
    {
        _cpu = cpu;
        _modem = modem;
        _diagnostics = diagnostics;
        _paceToRealTime = paceToRealTime;
        _persistenceStore = persistenceStore;
        _persistenceKey = persistenceKey;
    }

    public event EventHandler<MainViewTextEventArgs>? StatusChanged;

    public event EventHandler<MainViewTextEventArgs>? OutputReceived;

    internal void RaiseStatusChanged(string status) =>
        StatusChanged?.Invoke(this, new(status));

    public event EventHandler<MainViewTextEventArgs>? GsmMessageEmitted;

    internal void RaiseGsmMessageEmitted(string message) =>
        GsmMessageEmitted?.Invoke(this, new(message));

    public event EventHandler<MainViewFrameEventArgs>? FrameReady;

    internal void RaiseFrameReady(ReadOnlyMemory<byte> frame)
    {
        FrameReady?.Invoke(this, new(frame));
        if (Interlocked.Exchange(ref _runningNotified, 1) == 0)
        {
            RaiseStatusChanged("Running");
        }
    }

    public event EventHandler<MainViewAudioEventArgs>? AudioReady;

    internal void RaiseAudioReady(ReadOnlyMemory<short> samples) =>
        AudioReady?.Invoke(this, new(samples));

    public event EventHandler<MainViewStatusLedEventArgs>? StatusLedsChanged;

    internal void RaiseStatusLedsChanged(MiaStatusLedState state) =>
        StatusLedsChanged?.Invoke(this, new(
            state.BluetoothBlue,
            state.NetworkGreen));

    public event EventHandler<MainViewPhoneLedEventArgs>? PhoneLedsChanged;

    internal void RaisePhoneLedsChanged(MiaPhoneLedState state) =>
        PhoneLedsChanged?.Invoke(this, new(
            state.LeftRed,
            state.LeftGreen,
            state.RightBlue,
            state.BacklightsOn));

    public event EventHandler<MainViewBluetoothStatusEventArgs>?
        BluetoothStatusChanged;

    internal void RaiseBluetoothStatusChanged(
        MiaBluetoothEmulationStatus state) =>
        BluetoothStatusChanged?.Invoke(this, new()
        {
            Enabled = state.Enabled,
            PeerName = state.PeerName,
            PeerAddress = state.PeerAddress,
            InquiryResponseCount = ClampCount(state.InquiryResponseCount),
            RemoteNameResponseCount = ClampCount(state.RemoteNameResponseCount),
            PairingCompletedCount = ClampCount(state.PairingCompletedCount),
            ConnectionCompletedCount = ClampCount(state.ConnectionCompletedCount),
            SdpRequestCount = ClampCount(state.SdpRequestCount),
            ObexPutRequestCount = ClampCount(state.ObexPutRequestCount),
            TransferredObjectByteCount = ClampCount(
                state.TransferredObjectByteCount),
            IncomingObjectPushCompletedCount = ClampCount(
                state.IncomingObjectPushCompletedCount),
            PairingAuthenticationFailureCount = ClampCount(
                state.PairingAuthenticationFailureCount),
            ConnectionAuthenticationFailureCount = ClampCount(
                state.ConnectionAuthenticationFailureCount),
        });

    public event EventHandler<MainViewInfraredStatusEventArgs>?
        InfraredStatusChanged;

    internal void RaiseInfraredStatusChanged(
        MiaInfraredTransferStatus status) =>
        InfraredStatusChanged?.Invoke(this, new(
            status.Message,
            status.ReceivedObject.HasValue));

    public event EventHandler<MainViewCommuniCamEventArgs>?
        CommuniCamConnectionChanged;

    public bool IsRunning =>
        _runTask is { IsCompleted: false } &&
        Volatile.Read(ref _worker)?.IsRunning == true;

    public bool IsCommuniCamConnected => false;

    public bool IsCommuniCamOperationPending => false;

    public bool IsHostCameraAvailable => false;

    public string? HostCameraUnavailableReason =>
        "Host camera capture is unavailable in the browser runtime.";

    public async Task StartAsync()
    {
        await StopAsync(notify: false).ConfigureAwait(true);
        MiaPersistenceSnapshot persistenceSnapshot =
            _persistenceStore is not null && _persistenceKey is not null
                ? await _persistenceStore.LoadAsync(
                    _persistenceKey,
                    CancellationToken.None).ConfigureAwait(true) ?? MiaPersistenceSnapshot.Empty
                : MiaPersistenceSnapshot.Empty;
        var worker = new BrowserEmulatorSessionWorker(
            this,
            _cpu ?? throw new InvalidOperationException(
                "The browser emulator session cannot be started again."),
            _modem ?? throw new InvalidOperationException(
                "The browser emulator session cannot be started again."),
            persistenceSnapshot,
            _persistenceStore,
            _persistenceKey,
            _paceToRealTime,
            _diagnostics);
        _cpu = null;
        _modem = null;
        Volatile.Write(ref _worker, worker);
        Volatile.Write(ref _runningNotified, 0);
        worker.Start();
        _runTask = worker.Completion;
        await worker.Started.ConfigureAwait(true);
        CommuniCamConnectionChanged?.Invoke(this, new(false));
    }

    public Task ConnectCommuniCamAsync() => Task.FromException(
        new NotSupportedException(HostCameraUnavailableReason));

    public Task DisconnectCommuniCamAsync() => Task.CompletedTask;

    public void SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed)
    {
        Volatile.Read(ref _worker)?.Post(machine =>
            _ = ObserveInputAsync(machine.SetKeyAsync(
                    scanMask,
                    rowMask,
                    secondaryScanMask,
                    pressed),
                $"scan=0x{scanMask:x2} row=0x{rowMask:x2} " +
                $"{(pressed ? "down" : "up")}"));
    }

    public void SetPowerKey(bool pressed)
    {
        Volatile.Read(ref _worker)?.Post(machine =>
            _ = ObserveInputAsync(
                machine.SetPowerKeyAsync(pressed),
                $"power {(pressed ? "down" : "up")}"));
    }

    public bool TryQueueIncomingSms(string originator, string text, out string result)
    {
        return TryInvokeGsmCommand(
            liveGsm =>
            {
                bool success = liveGsm.TryQueueIncomingSms(
                    originator,
                    text,
                    out string message);
                return (success, message);
            },
            out result);
    }

    public bool TryQueueIncomingCall(
        string originator,
        bool autoAnswer,
        out string result)
    {
        return TryInvokeGsmCommand(
            liveGsm =>
            {
                bool success = liveGsm.TryQueueIncomingCall(
                    originator,
                    autoAnswer,
                    out string message);
                return (success, message);
            },
            out result);
    }

    public bool TrySetCarrierRawSample(int rawSample, out string result)
    {
        bool valid = rawSample is >= ushort.MinValue and <= ushort.MaxValue;
        var commandApplied = false;
        bool applied = valid && TryInvokeMachine(machine =>
            {
                if (machine.LiveGsm is not { } liveGsm)
                {
                    return false;
                }
                liveGsm.CarrierRawSample = (ushort)rawSample;
                return true;
            },
            out commandApplied) && commandApplied;
        result = (valid, applied) switch
        {
            (false, _) => "RSSI raw sample must be between 0 and 65535.",
            (true, false) => "The emulator is not running.",
            _ => $"Serving-carrier RSSI set to {rawSample}.",
        };
        return applied;
    }

    bool TryInvokeGsmCommand(
        Func<MiaLiveGsmController, (bool Success, string Message)> command,
        out string result)
    {
        bool invoked = TryInvokeMachine(
            machine => machine.LiveGsm is { } liveGsm
                ? command(liveGsm)
                : (false, "The emulator is not running."),
            out (bool Success, string Message) response);
        response = invoked
            ? response
            : (false, "The emulator is not running.");
        result = response.Message;
        return response.Success;
    }

    public bool TryConfigureBluetoothPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled,
        out string result)
    {
        if (string.IsNullOrEmpty(pin) ||
            pin.Length > 10 ||
            pin.Any(character => character is < '0' or > '9'))
        {
            result = "Pairing PIN must contain 1 to 10 digits.";
            return false;
        }

        var previousConfiguration = Volatile.Read(ref _bluetoothConfiguration);
        Volatile.Write(ref _bluetoothConfiguration, previousConfiguration with
        {
            PeerEnabled = enabled,
            PeerPin = pin,
            IncomingObjectPushEnabled = incomingObjectPushEnabled,
        });

        if (!TryInvokeMachine(machine =>
            {
                machine.ConfigureEmulatedBluetoothPeer(
                    enabled,
                    pin,
                    incomingObjectPushEnabled);
                return machine.GetBluetoothEmulationStatus();
            },
            out MiaBluetoothEmulationStatus status))
        {
            result = enabled
                ? "Emulated peer settings saved for the next boot."
                : "Emulated peer will remain disabled on the next boot.";
            return true;
        }
        RaiseBluetoothStatusChanged(status);
        result = enabled
            ? $"Emulated peer ready · PIN {pin} · pair from the handset."
            : "Emulated Bluetooth peer disabled.";
        return true;
    }

    public bool TryStageBluetoothObject(
        string name,
        string mediaType,
        ReadOnlyMemory<byte> data,
        out string result)
    {
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            result = "Bluetooth file names must contain 1 to 64 characters.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(mediaType) ||
            mediaType.Length > 64 ||
            mediaType.Any(character => character > 0x7f))
        {
            result = "Bluetooth media types must contain 1 to 64 ASCII characters.";
            return false;
        }

        var transferObject = new MiaTransferObject(
            name,
            mediaType,
            data.ToArray());
        var previousConfiguration = Volatile.Read(ref _bluetoothConfiguration);
        Volatile.Write(ref _bluetoothConfiguration, previousConfiguration with
        {
            StagedObject = transferObject,
            IncomingObjectPushEnabled = true,
        });

        Volatile.Read(ref _worker)?.Post(machine =>
            machine.StageEmulatedBluetoothObject(transferObject));
        result =
            $"Staged {name} · {data.Length:n0} B. Send an item from the handset " +
            "to Mia Peer; the peer returns this file on the same native link.";
        return true;
    }

    public bool TryGetBluetoothReceivedObject(
        out MainViewTransferObject value,
        out string result)
    {
        if (!TryInvokeMachine(
            machine => machine.GetBluetoothReceivedObject()?.Snapshot(),
            out MiaTransferObject? received))
        {
            value = default;
            result = "The emulator stopped before the Bluetooth object was read.";
            return false;
        }
        if (received is not { } available)
        {
            value = default;
            result = "No Bluetooth object has been received from the handset.";
            return false;
        }
        value = MapTransfer(available);
        result = $"Bluetooth received {value.Name} · {value.Data.Length:n0} B.";
        return true;
    }

    public bool TryStageInfraredObject(
        MainViewTransferObject value,
        out string result)
    {
        _stagedInfraredObject = new MiaTransferObject(
            value.Name,
            value.MediaType,
            value.Data).Snapshot();
        if (!TryInvokeMachine(machine =>
            {
                machine.StageEmulatedInfraredObject(_stagedInfraredObject);
                return true;
            },
            out _))
        {
            result = "The emulator is not running.";
            return false;
        }
        result =
            $"Staged {value.Name} · {value.Data.Length:n0} B for IrDA. " +
            "Open Connect → Receive item on the handset.";
        return true;
    }

    public bool TryGetInfraredReceivedObject(
        out MainViewTransferObject value,
        out string result)
    {
        if (!TryInvokeMachine(
            machine => machine.GetInfraredReceivedObject()?.Snapshot(),
            out MiaTransferObject? received))
        {
            value = default;
            result = "The emulator stopped before the IrDA object was read.";
            return false;
        }
        if (received is not { } available)
        {
            value = default;
            result = "No IrDA object has been received from the handset.";
            return false;
        }
        value = MapTransfer(available);
        result = $"IrDA received {value.Name} · {value.Data.Length:n0} B.";
        return true;
    }

    public Task StopAsync() => StopAsync(notify: true);

    public Task StopForRestartAsync() => StopAsync(notify: false);

    async Task StopAsync(bool notify)
    {
        var worker = Interlocked.Exchange(ref _worker, null);
        var runTask = _runTask;
        _runTask = null;
        worker?.Stop();
        if (runTask is not null)
        {
            await runTask.ConfigureAwait(true);
        }
        if (notify)
        {
            RaiseStatusLedsChanged(default);
            RaisePhoneLedsChanged(default);
            RaiseStatusChanged("Stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(true);
    }

    async Task ObserveInputAsync(
        ValueTask<InteractiveKeypadTransition> transition,
        string description)
    {
        try
        {
            var result = await transition.ConfigureAwait(false);
            if (_diagnostics)
            {
                Console.WriteLine(
                    $"Mia browser input: {description} " +
                    $"{result.ToString().ToUpperInvariant()}");
            }
        }
        catch (ObjectDisposedException)
        {
            // A queued edge can race an explicit browser restart or shutdown.
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is InvalidOperationException or
            IOException)
        {
            RaiseStatusChanged($"Input failed · {error.Message}");
            await Console.Error.WriteLineAsync(
                $"Mia browser input failed ({description}): {error}").ConfigureAwait(false);
        }
    }

    internal void ApplyBluetoothConfiguration(MiaMachine machine)
    {
        var configuration = Volatile.Read(ref _bluetoothConfiguration);
        machine.ConfigureEmulatedBluetoothPeer(
            configuration.PeerEnabled,
            configuration.PeerPin,
            configuration.IncomingObjectPushEnabled);
        if (configuration.StagedObject.HasValue)
        {
            machine.StageEmulatedBluetoothObject(configuration.StagedObject);
        }
    }

    internal void ApplyStagedInfraredObject(MiaMachine machine) =>
        machine.StageEmulatedInfraredObject(_stagedInfraredObject);

    internal void ReportWorkerFailure(Exception error)
    {
        BrowserDispatcher.Post(() =>
        {
            OutputReceived?.Invoke(this, new(error.ToString()));
            RaiseStatusChanged($"Emulator failed · {error.Message}");
        });
        Console.Error.WriteLine(
            $"Mia browser emulator worker failed: {error}");
    }

    static int ClampCount(long value) =>
        (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    static MainViewTransferObject MapTransfer(MiaTransferObject value) =>
        new(value.Name, value.MediaType, value.Data);

    internal bool ReportWorkerCommandFailure(Exception error)
    {
        BrowserDispatcher.Post(() =>
        {
            OutputReceived?.Invoke(this, new(error.ToString()));
            RaiseStatusChanged($"Emulator command failed · {error.Message}");
        });
        Console.Error.WriteLine(
            $"Mia browser emulator command failed: {error}");
        return true;
    }

    bool TryInvokeMachine<TResult>(
        Func<MiaMachine, TResult> function,
        out TResult value)
    {
        var worker = Volatile.Read(ref _worker);
        if (worker is null)
        {
            value = default!;
            return false;
        }
        try
        {
            return worker.TryInvoke(function, out value);
        }
        catch (Exception error) when (
            error is InvalidOperationException or ObjectDisposedException)
        {
            value = default!;
            ReportWorkerCommandFailure(error);
            return false;
        }
    }

}
