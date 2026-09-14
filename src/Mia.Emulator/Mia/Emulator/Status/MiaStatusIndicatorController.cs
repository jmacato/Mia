// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Status;

/// <summary>
/// Converts native firmware behavior into the two physical status concepts.
/// Green follows the completed GSM registration boundary while MPH remains in
/// a serving state. Blue follows native Bluetooth activity and is held steady
/// when the controller is on while the native charger measurement path is
/// active. No A5 or secondary-I2C latch is interpreted as an indicator output.
/// </summary>
internal sealed class MiaStatusIndicatorController
{
    public const byte BluetoothControllerDestination = 0x86;
    public const byte BluetoothApplicationSource = 0x5c;
    public const ushort BluetoothOperationSignal = 0xd2de;
    public const ushort BluetoothOperationCompletionSignal = 0xd306;
    public const long BluetoothActivityDurationCycles = 3_250_000;
    public const byte BluetoothGlobalStateCommandType = 0x25;
    public const byte BluetoothGlobalOnState = 0x80;
    public const byte BluetoothFirstLinkStateCommandType = 0x85;
    public const int BluetoothLinkSlotCount = 8;

    const int MaximumNativeLinkPayload = 4096;
    static ReadOnlySpan<byte> BluetoothOperationCompletionRecord =>
        [0xdc, 0x03, 0x00, 0x06, 0x06, 0xd3, 0x00];

    readonly byte[] _asicPayloadPrefix = new byte[3];
    readonly byte[] _modemCompletionWindow =
        new byte[BluetoothOperationCompletionRecord.Length];
    MiaStatusLedState _state;
    bool _networkRegistered;
    byte _nativeMphState;
    long _networkStateChangeCount;
    long _bluetoothControllerRecordCount;
    long _bluetoothOperationRequestCount;
    long _bluetoothOperationCompletionCount;
    long _bluetoothDspPacketCount;
    long _bluetoothDspTransferCount;
    byte _lastBluetoothOperationValue;
    MiaBluetoothControllerStates _bluetoothControllerStates;
    long _bluetoothGlobalStateCommandCount;
    long _bluetoothLinkStateCommandCount;
    long _bluetoothActivityDeadlineCycle;
    bool _bluetoothActivityPending;
    bool _chargingActive;
    bool _bluetoothSteadyDueToCharging;
    int _asicFrameState;
    byte _asicFrameDestination;
    byte _asicFrameSource;
    int _asicFrameLength;
    int _asicPayloadIndex;
    int _modemCompletionWindowCount;
    ArmModemBus? _modemBus;
    ArmModemBluetoothPeripheral? _bluetoothPeripheral;
    MiaPowerPortController? _powerPorts;
    MiaSystemClock? _clock;
    MiaWorker? _worker;
    Action? _bluetoothActivityDeadline;
    int _bluetoothActivitySchedulePending;

    public MiaStatusLedState State
    {
        get
        {
            MiaWorker? worker = Volatile.Read(ref _worker);
            if (worker is null || worker.IsCurrentThread)
            {
                return _state;
            }
            return worker.Invoke(() => _state);
        }
    }

    public MiaStatusIndicatorDiagnostics Diagnostics
    {
        get
        {
            MiaWorker? worker = Volatile.Read(ref _worker);
            if (worker is null || worker.IsCurrentThread)
            {
                return DiagnosticsCore();
            }
            return worker.Invoke(DiagnosticsCore);
        }
    }

    MiaStatusIndicatorDiagnostics DiagnosticsCore() => new(
        _state,
        _networkRegistered,
        _nativeMphState,
        _networkStateChangeCount,
        _bluetoothControllerRecordCount,
        _bluetoothOperationRequestCount,
        _bluetoothOperationCompletionCount,
        _bluetoothDspPacketCount,
        _bluetoothDspTransferCount,
        _lastBluetoothOperationValue,
        _chargingActive,
        _bluetoothSteadyDueToCharging,
        _bluetoothControllerStates,
        _bluetoothGlobalStateCommandCount,
        _bluetoothLinkStateCommandCount);

    public event Action<MiaStatusLedState>? StateChanged;

    internal void Attach(MiaStatusIndicatorDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArmModemBus modemBus = dependencies.ModemBus;
        ArmModemBluetoothPeripheral bluetoothPeripheral =
            dependencies.BluetoothPeripheral;
        MiaPowerPortController powerPorts = dependencies.PowerPorts;
        MiaSystemClock clock = dependencies.Clock;
        MiaWorker clockWorker = dependencies.ClockWorker;
        ArgumentNullException.ThrowIfNull(modemBus);
        ArgumentNullException.ThrowIfNull(bluetoothPeripheral);
        ArgumentNullException.ThrowIfNull(powerPorts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(clockWorker);
        // Attach can only ever be called once, synchronously, before this
        // controller's reference is shared with any other thread (its
        // Observe* handlers aren't wired to any event source until after
        // this method returns), so no synchronization is needed here.
        if (_modemBus is not null ||
            _bluetoothPeripheral is not null ||
            _powerPorts is not null)
        {
            throw new InvalidOperationException(
                "The status indicator controller is already attached to a modem.");
        }
        _modemBus = modemBus;
        _bluetoothPeripheral = bluetoothPeripheral;
        _powerPorts = powerPorts;
        _clock = clock;
        Volatile.Write(ref _worker, clockWorker);
        _chargingActive = powerPorts.ChargingActive;
        modemBus.Uart1ByteReceived += ObserveAsicToModemByte;
        modemBus.Uart1ByteTransmitted += ObserveModemToAsicByte;
        modemBus.DspPacketTransmitted += ObserveBluetoothDspPacket;
        modemBus.DspTransferTransmitted += ObserveBluetoothDspTransfer;
        bluetoothPeripheral.DspCommandQueued +=
            ObserveBluetoothControllerCommand;
        powerPorts.ChargingStateChanged += ObserveChargingBoundary;
    }

    internal void Detach()
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null)
        {
            return;
        }

        (ArmModemBus? modemBus,
            ArmModemBluetoothPeripheral? bluetoothPeripheral,
            MiaPowerPortController? powerPorts,
            MiaSystemClock? clock,
            Action? activityDeadline) = worker.IsCurrentThread
                ? DetachCore()
                : worker.Invoke(DetachCore);

        if (modemBus is null)
        {
            return;
        }
        DetachSubscriptions(modemBus, bluetoothPeripheral, powerPorts);
        if (clock is not null && activityDeadline is not null)
        {
            worker.Invoke(() => clock.Cancel(activityDeadline));
        }
    }

    void DetachSubscriptions(
        ArmModemBus modemBus,
        ArmModemBluetoothPeripheral? bluetoothPeripheral,
        MiaPowerPortController? powerPorts)
    {
        modemBus.Uart1ByteReceived -= ObserveAsicToModemByte;
        modemBus.Uart1ByteTransmitted -= ObserveModemToAsicByte;
        modemBus.DspPacketTransmitted -= ObserveBluetoothDspPacket;
        modemBus.DspTransferTransmitted -= ObserveBluetoothDspTransfer;
        if (bluetoothPeripheral is not null)
        {
            bluetoothPeripheral.DspCommandQueued -=
                ObserveBluetoothControllerCommand;
        }
        if (powerPorts is not null)
        {
            powerPorts.ChargingStateChanged -= ObserveChargingBoundary;
        }
    }

    (ArmModemBus?, ArmModemBluetoothPeripheral?, MiaPowerPortController?,
        MiaSystemClock?, Action?) DetachCore()
    {
        var modemBus = _modemBus;
        _modemBus = null;
        var bluetoothPeripheral = _bluetoothPeripheral;
        _bluetoothPeripheral = null;
        var powerPorts = _powerPorts;
        _powerPorts = null;
        var clock = _clock;
        _clock = null;
        Volatile.Write(ref _worker, null);
        var activityDeadline = _bluetoothActivityDeadline;
        _bluetoothActivityDeadline = null;
        return (modemBus, bluetoothPeripheral, powerPorts, clock, activityDeadline);
    }

    internal void ObserveNetworkBoundary(bool registered, byte nativeMphState)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveNetworkBoundaryCore(registered, nativeMphState);
        }
        else
        {
            worker.Post(() =>
                ObserveNetworkBoundaryCore(registered, nativeMphState));
        }
    }

    void ObserveNetworkBoundaryCore(bool registered, byte nativeMphState)
    {
        MiaStatusLedState? changedState = null;
        {
            _networkRegistered = registered;
            _nativeMphState = nativeMphState;
            bool networkGreen = HasRegisteredService(
                registered,
                nativeMphState);
            if (networkGreen != _state.NetworkGreen)
            {
                _state = _state with { NetworkGreen = networkGreen };
                _networkStateChangeCount++;
                changedState = _state;
            }
        }
        Publish(changedState);
    }

    internal static bool HasRegisteredService(
        bool registered,
        byte nativeMphState) =>
        registered && nativeMphState is
            >= 0x03 and <= 0x11 and not 0x07 and not 0x08;

    internal void SynchronizeTestClock(long currentCycle)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentCycle);
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            SynchronizeTestClockCore(currentCycle);
        }
        else
        {
            worker.Invoke(() => SynchronizeTestClockCore(currentCycle));
        }
    }

    void SynchronizeTestClockCore(long currentCycle)
    {
        MiaStatusLedState? changedState = _bluetoothSteadyDueToCharging
            ? SynchronizeChargingState()
            : SynchronizeActivityState(currentCycle);
        Publish(changedState);
    }

    MiaStatusLedState? SynchronizeChargingState()
    {
        _bluetoothActivityPending = false;
        if (_state.BluetoothBlue)
        {
            return null;
        }
        _state = _state with { BluetoothBlue = true };
        return _state;
    }

    MiaStatusLedState? SynchronizeActivityState(long currentCycle)
    {
        if (_bluetoothActivityPending)
        {
            _bluetoothActivityPending = false;
            _bluetoothActivityDeadlineCycle = currentCycle <=
                long.MaxValue - BluetoothActivityDurationCycles
                    ? currentCycle + BluetoothActivityDurationCycles
                    : long.MaxValue;
            return null;
        }
        if (!_state.BluetoothBlue ||
            currentCycle < _bluetoothActivityDeadlineCycle)
        {
            return null;
        }
        _state = _state with { BluetoothBlue = false };
        return _state;
    }

    internal void ObserveAsicToModemByte(byte value)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveAsicToModemByteCore(value);
        }
        else
        {
            worker.Post(() => ObserveAsicToModemByteCore(value));
        }
    }

    void ObserveAsicToModemByteCore(byte value)
    {
        MiaStatusLedState? changedState = _asicFrameState switch
        {
            0 => ObserveAsicSync(value),
            1 => ObserveAsicSyncComplement(value),
            2 => ObserveAsicDestination(value),
            3 => ObserveAsicLengthLow(value),
            4 => ObserveAsicLengthHigh(value),
            5 => ObserveAsicSource(value),
            6 => ObserveAsicPayload(value),
            _ => null,
        };
        Publish(changedState);
    }

    MiaStatusLedState? ObserveAsicSync(byte value)
    {
        if (value == 0xab)
        {
            _asicFrameState = 1;
        }
        return null;
    }

    MiaStatusLedState? ObserveAsicSyncComplement(byte value)
    {
        _asicFrameState = value switch
        {
            0xba => 2,
            0xab => 1,
            _ => 0,
        };
        return null;
    }

    MiaStatusLedState? ObserveAsicDestination(byte value)
    {
        _asicFrameDestination = value;
        _asicFrameState = 3;
        return null;
    }

    MiaStatusLedState? ObserveAsicLengthLow(byte value)
    {
        _asicFrameLength = value;
        _asicFrameState = 4;
        return null;
    }

    MiaStatusLedState? ObserveAsicLengthHigh(byte value)
    {
        _asicFrameLength |= value << 8;
        if (_asicFrameLength > MaximumNativeLinkPayload)
        {
            ResetAsicFrame(value);
        }
        else
        {
            _asicFrameState = 5;
        }
        return null;
    }

    MiaStatusLedState? ObserveAsicSource(byte value)
    {
        _asicFrameSource = value;
        _asicPayloadIndex = 0;
        if (_asicFrameLength == 0)
        {
            return CompleteAsicFrame();
        }
        _asicFrameState = 6;
        return null;
    }

    MiaStatusLedState? ObserveAsicPayload(byte value)
    {
        if (_asicPayloadIndex < _asicPayloadPrefix.Length)
        {
            _asicPayloadPrefix[_asicPayloadIndex] = value;
        }
        _asicPayloadIndex++;
        return _asicPayloadIndex == _asicFrameLength
            ? CompleteAsicFrame()
            : null;
    }

    internal void ObserveModemToAsicByte(byte value)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveModemToAsicByteCore(value);
        }
        else
        {
            worker.Post(() => ObserveModemToAsicByteCore(value));
        }
    }

    void ObserveModemToAsicByteCore(byte value)
    {
        MiaStatusLedState? changedState = null;
        {
            if (_modemCompletionWindowCount < _modemCompletionWindow.Length)
            {
                _modemCompletionWindow[_modemCompletionWindowCount++] = value;
            }
            else
            {
                _modemCompletionWindow.AsSpan(1).CopyTo(_modemCompletionWindow);
                _modemCompletionWindow[^1] = value;
            }

            if (_modemCompletionWindowCount == _modemCompletionWindow.Length &&
                _modemCompletionWindow.AsSpan().SequenceEqual(
                    BluetoothOperationCompletionRecord))
            {
                _bluetoothOperationCompletionCount++;
                changedState = PulseBluetoothActivity();
                _modemCompletionWindowCount = 0;
            }
        }
        Publish(changedState);
    }

    internal void ObserveBluetoothDspPacket(ArmModemDspPacket packet)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveBluetoothDspPacketCore();
        }
        else
        {
            worker.Post(ObserveBluetoothDspPacketCore);
        }
    }

    void ObserveBluetoothDspPacketCore()
    {
        MiaStatusLedState? changedState;
        {
            _bluetoothDspPacketCount++;
            changedState = PulseBluetoothActivity();
        }
        Publish(changedState);
    }

    internal void ObserveBluetoothControllerCommand(ArmModemDspPacket command)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveBluetoothControllerCommandCore(command);
        }
        else
        {
            worker.Post(() => ObserveBluetoothControllerCommandCore(command));
        }
    }

    void ObserveBluetoothControllerCommandCore(ArmModemDspPacket command)
    {
        MiaStatusLedState? changedState;
        {
            CollectBluetoothControllerState(command);
            changedState = PulseBluetoothActivity();
            changedState = UpdateBluetoothSteadyState() ?? changedState;
        }
        Publish(changedState);
    }

    internal void ObserveChargingBoundary(bool active)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveChargingBoundaryCore(active);
        }
        else
        {
            worker.Post(() => ObserveChargingBoundaryCore(active));
        }
    }

    void ObserveChargingBoundaryCore(bool active)
    {
        MiaStatusLedState? changedState;
        {
            _chargingActive = active;
            changedState = UpdateBluetoothSteadyState();
        }
        Publish(changedState);
    }

    void CollectBluetoothControllerState(ArmModemDspPacket packet)
    {
        if (packet.Payload.Length != 1)
        {
            return;
        }

        byte state = packet.Payload[0];
        if (packet.Type == BluetoothGlobalStateCommandType)
        {
            _bluetoothControllerStates = _bluetoothControllerStates with
            {
                HasGlobalState = true,
                GlobalState = state,
            };
            _bluetoothGlobalStateCommandCount++;
            return;
        }

        if (!TryDecodeLinkSlot(packet.Type, out int slot))
        {
            return;
        }

        int shift = slot * 8;
        ulong stateMask = 0xffUL << shift;
        _bluetoothControllerStates = _bluetoothControllerStates with
        {
            ObservedLinkSlotMask = (byte)(
                _bluetoothControllerStates.ObservedLinkSlotMask |
                1 << slot),
            PackedLinkStates =
                (_bluetoothControllerStates.PackedLinkStates & ~stateMask) |
                (ulong)state << shift,
        };
        _bluetoothLinkStateCommandCount++;
    }

    static bool TryDecodeLinkSlot(byte packetType, out int slot)
    {
        int encodedSlot = packetType - BluetoothFirstLinkStateCommandType;
        slot = encodedSlot / 0x10;
        return encodedSlot >= 0 &&
            encodedSlot % 0x10 == 0 &&
            slot < BluetoothLinkSlotCount;
    }

    internal void ObserveBluetoothDspTransfer(ArmModemDspTransfer _)
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            ObserveBluetoothDspTransferCore();
        }
        else
        {
            worker.Post(ObserveBluetoothDspTransferCore);
        }
    }

    void ObserveBluetoothDspTransferCore()
    {
        MiaStatusLedState? changedState;
        {
            _bluetoothDspTransferCount++;
            changedState = PulseBluetoothActivity();
        }
        Publish(changedState);
    }

    MiaStatusLedState? CompleteAsicFrame()
    {
        MiaStatusLedState? changedState = null;
        if (_asicFrameDestination == BluetoothControllerDestination)
        {
            changedState = RecordBluetoothControllerFrame();
        }
        ResetAsicFrame();
        return changedState;
    }

    MiaStatusLedState? RecordBluetoothControllerFrame()
    {
        _bluetoothControllerRecordCount++;
        MiaStatusLedState? changedState = PulseBluetoothActivity();
        if (_asicFrameSource == BluetoothApplicationSource &&
            _asicFrameLength >= 3 &&
            (_asicPayloadPrefix[0] |
             _asicPayloadPrefix[1] << 8) == BluetoothOperationSignal)
        {
            _bluetoothOperationRequestCount++;
            _lastBluetoothOperationValue = _asicPayloadPrefix[2];
        }
        return changedState;
    }

    MiaStatusLedState? PulseBluetoothActivity()
    {
        _bluetoothActivityPending = true;
        QueueBluetoothActivitySchedule();
        if (_state.BluetoothBlue)
        {
            return null;
        }
        _state = _state with { BluetoothBlue = true };
        return _state;
    }

    void QueueBluetoothActivitySchedule()
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null ||
            Interlocked.CompareExchange(
                ref _bluetoothActivitySchedulePending,
                1,
                0) != 0)
        {
            return;
        }
        worker.Post(ApplyPendingBluetoothActivity);
    }

    void ApplyPendingBluetoothActivity()
    {
        // Always reached via worker.Post from QueueBluetoothActivitySchedule,
        // so this already runs on the owner worker's thread.
        Interlocked.Exchange(ref _bluetoothActivitySchedulePending, 0);
        if (!_bluetoothActivityPending || _clock is null)
        {
            return;
        }
        _bluetoothActivityPending = false;
        ScheduleBluetoothActivityDeadline();
    }

    void ScheduleBluetoothActivityDeadline()
    {
        if (_clock is null)
        {
            return;
        }
        if (_bluetoothActivityDeadline is not null)
        {
            _clock.Cancel(_bluetoothActivityDeadline);
        }
        _bluetoothActivityDeadline = OnBluetoothActivityDeadline;
        _bluetoothActivityDeadlineCycle =
            _clock.Cycles + BluetoothActivityDurationCycles;
        _clock.Schedule(
            _bluetoothActivityDeadline,
            BluetoothActivityDurationCycles);
    }

    void OnBluetoothActivityDeadline()
    {
        MiaWorker? worker = Volatile.Read(ref _worker);
        if (worker is null || worker.IsCurrentThread)
        {
            OnBluetoothActivityDeadlineCore();
        }
        else
        {
            worker.Invoke(OnBluetoothActivityDeadlineCore);
        }
    }

    void OnBluetoothActivityDeadlineCore()
    {
        MiaStatusLedState? changedState = null;
        {
            _bluetoothActivityDeadline = null;
            if (_bluetoothActivityPending)
            {
                _bluetoothActivityPending = false;
                ScheduleBluetoothActivityDeadline();
            }
            else if (!_bluetoothSteadyDueToCharging &&
                     _state.BluetoothBlue)
            {
                _state = _state with { BluetoothBlue = false };
                changedState = _state;
            }
        }
        Publish(changedState);
    }

    MiaStatusLedState? UpdateBluetoothSteadyState()
    {
        bool steady =
            _chargingActive &&
            _bluetoothControllerStates.HasGlobalState &&
            _bluetoothControllerStates.GlobalState ==
                BluetoothGlobalOnState;
        bool wasSteady = _bluetoothSteadyDueToCharging;
        _bluetoothSteadyDueToCharging = steady;
        if (steady)
        {
            return EnterBluetoothSteadyState();
        }
        if (wasSteady &&
            !_bluetoothActivityPending &&
            _state.BluetoothBlue)
        {
            _state = _state with { BluetoothBlue = false };
            return _state;
        }
        return null;
    }

    MiaStatusLedState? EnterBluetoothSteadyState()
    {
        _bluetoothActivityPending = false;
        if (_state.BluetoothBlue)
        {
            return null;
        }
        _state = _state with { BluetoothBlue = true };
        return _state;
    }

    void ResetAsicFrame(byte possibleSync = 0)
    {
        _asicFrameState = possibleSync == 0xab ? 1 : 0;
        _asicFrameLength = 0;
        _asicPayloadIndex = 0;
        _asicFrameDestination = 0;
        _asicFrameSource = 0;
        Array.Clear(_asicPayloadPrefix);
    }

    void Publish(MiaStatusLedState? state)
    {
        if (state is { } value)
        {
            StateChanged?.Invoke(value);
        }
    }
}
