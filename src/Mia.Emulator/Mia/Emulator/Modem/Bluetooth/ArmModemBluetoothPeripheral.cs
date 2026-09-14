// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Bluetooth;

/// <summary>
/// Firmware-visible Bluetooth DSP boundary for the R8A015 modem.
///
/// The native controller asks the DSP to complete a fixed secondary transfer
/// by emitting packet <c>10 01 kk</c>.  The physical DSP acknowledges that
/// request through interrupt-status bit 08; no response payload is involved.
/// This peripheral implements that measured handshake and the observed DSP
/// frame cadence, then exposes recovered controller ingress through complete
/// 17-byte kind-3 records and kind-6/kind-5 L2CAP fragments. It also supplies one
/// deterministic emulated peer through the recovered inquiry, remote-name,
/// pairing, and connection transactions. No host Bluetooth radio is opened or
/// used.
/// </summary>
internal sealed class ArmModemBluetoothPeripheral : IDisposable
{
    public const string DefaultEmulatedPeerName = "Mia Peer";
    public const string DefaultEmulatedPeerAddress = "11:22:33:44:55:66";
    public const string DefaultEmulatedPeerPin = "0000";
    public const string DefaultEmulatedHandsetAddress = "00:D2:0C:EC:01:00";
    public const int ControllerRecordLength = 0x11;
    public const byte ControllerRecordTransferKind = 3;
    public const byte FixedTransferReadyPacketType = 0x10;
    public const int FixedTransferReadyPacketPayloadLength = 1;
    public const byte FixedTransferCompletionStatus = 0x08;
    const uint DspCommandRingBase = 0x014215ec;
    const uint DspCommandRingProducerOffset = 0x30;
    const uint DspCommandRingProducerAddress =
        ArmModemBus.ExternalRamBase + 0x0c40 + DspCommandRingProducerOffset;
    const int DspCommandRingSlots = 0x1e;
    const int DspCommandSlotLength = 0x13;
    const uint DspDriverStateAddress = 0x01400c96;
    const byte DspDriverResponseReadyState = 1;
    const uint LinkManagerActionDispatchInstruction = 0x010adb72;
    const ushort PairingSetupAction = 0x0016;
    const long DspFramePeriodCycles = 60_000;
    const long EmulatedInquirySettleCycles = 55_000_000;
    const long EmulatedResponseDelayCycles = 500_000;
    const long EmulatedNameRecordDelayCycles = 750_000;
    const long EmulatedPairingSetupDelayCycles = 500_000;
    const long EmulatedConnectionSetupDelayCycles = 3_000_000;
    const long EmulatedPairingRecordSpacingCycles = 8_000_000;
    const uint DspStatusAddress = 0x00800800;
    const byte DspResetStatusMask = 0x17;
    const byte DspPacketPortWriteReadyStatus = 0x20;
    const byte DspSecondaryPacketPortWriteReadyStatus = 0x20;
    const byte DefaultMeasurement = 0xd0;

    readonly ArmModemBus _bus;
    readonly ArmModem? _modem;
    readonly IDisposable? _linkManagerActionObserver;
    readonly IDisposable? _commandRingProducerObserver;
    readonly IDisposable? _dspDriverStateObserver;
    readonly PriorityQueue<bool, long> _pendingDspCompletions = new();
    readonly Queue<ArmModemBluetoothPeripheralPendingDspInboundTransfer> _pendingDspInboundTransfers = new();
    readonly PriorityQueue<byte[], long> _pendingPairingControllerRecords = new();
    static readonly byte[] EmulatedPeerAddressBytes =
        [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
    // Fallback captured as the claimant address in the native E1 call at
    // 010abb0e. The normal path replaces this from the boot type-28 command.
    static readonly byte[] DefaultEmulatedHandsetAddressBytes =
        [0x00, 0xd2, 0x0c, 0xec, 0x01, 0x00];
    readonly byte[] _emulatedHandsetAddress =
        DefaultEmulatedHandsetAddressBytes.ToArray();
    readonly byte[] _emulatedLinkKey = new byte[16];
    long _nextDspFrameCycle = long.MaxValue;
    long _pendingDspOutputKickCycle = long.MaxValue;
    int _dspFrameNumber;
    ArmModemBluetoothPeripheralEmulatedInquiryPhase _emulatedInquiryPhase;
    long _emulatedInquiryDueCycle = long.MaxValue;
    bool _emulatedRemoteNameRecordPending;
    long _emulatedRemoteNameRecordDueCycle = long.MaxValue;
    bool _emulatedRemoteNameResolved;
    ArmModemBluetoothPeripheralEmulatedPairingPhase _emulatedPairingPhase;
    long _emulatedPairingSetupDueCycle = long.MaxValue;
    BluetoothLegacyPairingController? _emulatedPairingController;
    BluetoothLegacyConnectionController? _emulatedConnectionController;
    BluetoothL2capController? _emulatedL2capController;
    string _emulatedPeerPin = DefaultEmulatedPeerPin;
    bool _emulatePeer = true;
    bool _emulateIncomingObjectPush = true;
    MiaTransferObject? _emulatedIncomingObject;
    bool _emulatedPairingAuthenticationFailureCounted;
    bool _resumeIdleTransfersAfterRemoteNameCommand;
    bool _emulatedBondedConnectionArmed;
    bool _emulatedIndexedEventInFlight;
    bool _suppressIdleTransfersForEmulatedTransaction;
    IDisposable? _peerServiceEvent;
    long _peerServiceCycle = long.MaxValue;
    bool _disposed;

    public ArmModemBluetoothPeripheral(ArmModemBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        // 010c079c polls this level at 0x0080000c before writing the
        // receiver-enable packet to the DSP packet port (0x00800804).
        // It is a DSP port-ready input, not an ARM interrupt cause.
        _bus.SetDspPacketPortStatusInput(
            DspPacketPortWriteReadyStatus,
            asserted: true);
        _bus.SetDspSecondaryPacketPortStatusInput(
            DspSecondaryPacketPortWriteReadyStatus,
            asserted: true);
        _bus.DspPacketTransmitted += OnDspPacketTransmitted;
        _bus.DspTransferTransmitted += OnDspTransferTransmitted;
    }

    /// <summary>
    /// Attaches to the complete native modem, including the BT_Ctrl-owned DSP
    /// command ring. This is the constructor used by <see cref="MiaMachine"/>.
    /// </summary>
    public ArmModemBluetoothPeripheral(ArmModem modem)
        : this(modem?.Bus ?? throw new ArgumentNullException(nameof(modem)))
    {
        _modem = modem;
        InitializeIndexedRegisters();
        _linkManagerActionObserver = _modem.ObserveInstruction(
            LinkManagerActionDispatchInstruction,
            OnLinkManagerActionDispatch);
        _commandRingProducerObserver = _bus.ObserveExternalRamWrite(
            DspCommandRingProducerAddress,
            OnDspCommandRingProducerWritten);
        _dspDriverStateObserver = _bus.ObserveExternalRamWrite(
            DspDriverStateAddress,
            OnDspDriverStateWritten);
        _bus.MmioAccessed += OnMmioAccessed;
        _bus.ExternalDspFrameDue += OnExternalDspFrameDue;
        _bus.DspInterruptStatusChanged += OnDspInterruptStatusChanged;
    }

    /// <summary>
    /// Number of native fixed-transfer requests completed through status bit
    /// 08. This is a transport diagnostic, not a Bluetooth activity count.
    /// </summary>
    public long FixedTransferCompletionCount { get; private set; }

    /// <summary>
    /// Number of complete raw controller records supplied to the native DSP
    /// record decoder.
    /// </summary>
    public long ControllerRecordCount { get; private set; }

    /// <summary>
    /// Raised when BT_Ctrl commits an ARM-to-DSP command to its native output
    /// ring. The bytes are copied from the firmware-owned ring; this is not a
    /// host-created HCI abstraction.
    /// </summary>
    public event Action<ArmModemDspPacket>? DspCommandQueued;

    /// <summary>
    /// Raised from the ARM/controller owner when deterministic peer settings or
    /// protocol progress changes. The snapshot is captured on that owner.
    /// </summary>
    public event Action<MiaBluetoothEmulationStatus>? EmulationStatusChanged;

    public long DspCommandCount { get; private set; }

    /// <summary>
    /// Enables the built-in deterministic peer. This is entirely local
    /// controller emulation and never accesses a host Bluetooth adapter.
    /// </summary>
    public bool EmulatePeer
    {
        get => _emulatePeer;
        set
        {
            ThrowIfDisposed();
            if (_emulatePeer == value)
            {
                return;
            }
            _emulatePeer = value;
            PublishEmulationStatusChanged();
        }
    }

    public long EmulatedInquiryResponseCount { get; private set; }

    public long EmulatedRemoteNameResponseCount { get; private set; }

    public long EmulatedPairingSetupResponseCount { get; private set; }

    public long EmulatedPairingControllerRecordCount { get; private set; }

    public long EmulatedPairingCompletedCount { get; private set; }

    public long EmulatedPairingAuthenticationFailureCount { get; private set; }

    public long EmulatedConnectionSetupResponseCount { get; private set; }

    public long EmulatedConnectionControllerRecordCount { get; private set; }

    public long EmulatedConnectionCompletedCount { get; private set; }

    public long EmulatedConnectionAuthenticationFailureCount { get; private set; }

    public long EmulatedSdpServiceSearchAttributeRequestCount =>
        _emulatedL2capController?.SdpServiceSearchAttributeRequestCount ?? 0;

    public long EmulatedRfcommMultiplexerEstablishedCount =>
        _emulatedL2capController?.RfcommMultiplexerEstablishedCount ?? 0;

    public long EmulatedRfcommObjectPushLinkEstablishedCount =>
        _emulatedL2capController?.RfcommObjectPushLinkEstablishedCount ?? 0;

    public long EmulatedObexConnectRequestCount =>
        _emulatedL2capController?.ObexConnectRequestCount ?? 0;

    public long EmulatedObexPutRequestCount =>
        _emulatedL2capController?.ObexPutRequestCount ?? 0;

    public long EmulatedObexFinalPutRequestCount =>
        _emulatedL2capController?.ObexFinalPutRequestCount ?? 0;

    public long EmulatedObexDisconnectRequestCount =>
        _emulatedL2capController?.ObexDisconnectRequestCount ?? 0;

    public long EmulatedObexTransferredObjectByteCount =>
        _emulatedL2capController?.ObexTransferredObjectByteCount ?? 0;

    public ReadOnlyMemory<byte> EmulatedObexTransferredObject =>
        _emulatedL2capController?.ObexTransferredObject ??
        ReadOnlyMemory<byte>.Empty;

    public MiaTransferObject? EmulatedObexTransferredObjectSnapshot =>
        _emulatedL2capController?.ObexTransferredObjectSnapshot;

    public long EmulatedIncomingObjectPushStartedCount =>
        _emulatedL2capController?.IncomingObjectPushStartedCount ?? 0;

    public long EmulatedIncomingObjectPushCompletedCount =>
        _emulatedL2capController?.IncomingObjectPushCompletedCount ?? 0;

    public long EmulatedIncomingObjectPushDisconnectedCount =>
        _emulatedL2capController?.IncomingObjectPushDisconnectedCount ?? 0;

    /// <summary>
    /// Sends the deterministic peer's vCard back through the recovered native
    /// RFCOMM/OBEX server after the handset completes an outbound object push.
    /// This stays entirely inside controller emulation.
    /// </summary>
    public bool EmulateIncomingObjectPush
    {
        get => _emulateIncomingObjectPush;
        set
        {
            ThrowIfDisposed();
            if (_emulateIncomingObjectPush == value)
            {
                return;
            }
            _emulateIncomingObjectPush = value;
            PublishEmulationStatusChanged();
        }
    }

    /// <summary>
    /// PIN expected by the deterministic emulated peer. The native handset UI
    /// still supplies and processes its own PIN; this value configures only
    /// the remote side of the recovered pairing exchange.
    /// </summary>
    public string EmulatedPeerPin
    {
        get => _emulatedPeerPin;
        set
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(value) ||
                value.Length > 10 ||
                value.Any(character => character is < '0' or > '9'))
            {
                throw new ArgumentException(
                    "The emulated Bluetooth PIN must contain 1..10 digits.",
                    nameof(value));
            }
            if (_emulatedPeerPin == value)
            {
                return;
            }
            _emulatedPeerPin = value;
            PublishEmulationStatusChanged();
        }
    }

    public void ConfigureEmulatedPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(pin) ||
            pin.Length > 10 ||
            pin.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                "The emulated Bluetooth PIN must contain 1..10 digits.",
                nameof(pin));
        }

        bool changed =
            _emulatePeer != enabled ||
            _emulatedPeerPin != pin ||
            _emulateIncomingObjectPush != incomingObjectPushEnabled;
        _emulatePeer = enabled;
        _emulatedPeerPin = pin;
        _emulateIncomingObjectPush = incomingObjectPushEnabled;
        if (changed)
        {
            PublishEmulationStatusChanged();
        }
    }

    public void StageEmulatedIncomingObject(MiaTransferObject? value)
    {
        ThrowIfDisposed();
        _emulatedIncomingObject = value?.Snapshot();
        _emulateIncomingObjectPush = value.HasValue;
        PublishEmulationStatusChanged();
    }

    public MiaBluetoothEmulationStatus CaptureEmulationStatus() => new(
        EmulatePeer,
        DefaultEmulatedPeerName,
        DefaultEmulatedPeerAddress,
        EmulatedPeerPin,
        EmulateIncomingObjectPush,
        EmulatedInquiryResponseCount,
        EmulatedRemoteNameResponseCount,
        EmulatedPairingCompletedCount,
        EmulatedPairingAuthenticationFailureCount,
        EmulatedConnectionCompletedCount,
        EmulatedConnectionAuthenticationFailureCount,
        EmulatedSdpServiceSearchAttributeRequestCount,
        EmulatedObexPutRequestCount,
        EmulatedObexTransferredObjectByteCount,
        EmulatedIncomingObjectPushCompletedCount);

    /// <summary>
    /// Whether the built-in DSP source supplies empty inbound transfers at
    /// each frame boundary. Leave this enabled for the standalone emulator;
    /// an attached controller source can disable it so its data-ready IRQ
    /// remains quiescent between real controller messages.
    /// </summary>
    public bool EmitIdleTransferFrames { get; set; } = true;

    /// <summary>
    /// Delivers one complete controller record through the DSP-to-ARM FIFO.
    /// Callers must use a recovered record layout; partially filled records are
    /// rejected because the firmware copies all 17 bytes into an OSE signal.
    /// </summary>
    public void QueueControllerRecord(ReadOnlySpan<byte> record)
    {
        ThrowIfDisposed();
        if (record.Length != ControllerRecordLength)
        {
            throw new ArgumentException(
                $"Bluetooth controller records must be exactly " +
                $"{ControllerRecordLength} bytes.",
                nameof(record));
        }

        QueueDspInboundTransfer(
            0,
            ControllerRecordTransferKind,
            record);
        ControllerRecordCount++;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _bus.DspPacketTransmitted -= OnDspPacketTransmitted;
        _bus.DspTransferTransmitted -= OnDspTransferTransmitted;
        _bus.SetDspPacketPortStatusInput(
            DspPacketPortWriteReadyStatus,
            asserted: false);
        _bus.SetDspSecondaryPacketPortStatusInput(
            DspSecondaryPacketPortWriteReadyStatus,
            asserted: false);
        if (_modem is not null)
        {
            _linkManagerActionObserver?.Dispose();
            _commandRingProducerObserver?.Dispose();
            _dspDriverStateObserver?.Dispose();
            _peerServiceEvent?.Dispose();
            _bus.MmioAccessed -= OnMmioAccessed;
            _bus.ExternalDspFrameDue -= OnExternalDspFrameDue;
            _bus.DspInterruptStatusChanged -= OnDspInterruptStatusChanged;
        }
        _disposed = true;
    }

    void OnDspPacketTransmitted(ArmModemDspPacket packet)
    {
        // The DSP completes every native packet-port transaction through bit
        // 08. A 10,000,000-instruction modem trace with this completion path
        // keeps the full boot stream and repeated 45/25/1a/1b traffic moving;
        // acknowledging only 10 01 kk leaves the command ring stalled.
        bool isFixedTransfer = packet.Type == FixedTransferReadyPacketType &&
            packet.Payload.Length == FixedTransferReadyPacketPayloadLength;
        ScheduleDspCompletion(isFixedTransfer);
    }

    void OnDspTransferTransmitted(ArmModemDspTransfer transfer)
    {
        // Bit 08 completes framed controller and L2CAP payloads emitted by
        // 010c0312. Empty trailer-01 frame traffic is the DSP cadence itself,
        // not another acknowledged transaction; completing those frames
        // feeds status 08 back into the scheduler indefinitely.
        bool isControllerPayload =
            transfer.Kind == 7 &&
            transfer.Trailer == 3;
        bool isL2capPayload =
            transfer.Payload.Length != 0 &&
            transfer.Kind is
                BluetoothL2capController.InitialTransferKind or
                BluetoothL2capController.ContinuationTransferKind &&
            transfer.Trailer == 3;
        ScheduleDspCompletionIfPayload(isControllerPayload, isL2capPayload);

        if (!EmulatePeer ||
            _modem is null ||
            (!isControllerPayload && !isL2capPayload))
        {
            return;
        }
        ObserveEmulatedTransfer(transfer, isL2capPayload);
    }

    void ScheduleDspCompletionIfPayload(
        bool isControllerPayload,
        bool isL2capPayload)
    {
        if (isControllerPayload || isL2capPayload)
        {
            ScheduleDspCompletion(isFixedTransfer: false);
        }
    }

    void ObserveEmulatedTransfer(
        ArmModemDspTransfer transfer,
        bool isL2capPayload)
    {
        switch (isL2capPayload,
            _emulatedConnectionController is not null,
            _emulatedPairingController is not null)
        {
            case (true, _, _):
                ObserveEmulatedL2capTransfer(transfer);
                break;
            case (false, true, _):
                ObserveEmulatedConnectionTransfer(transfer.Payload);
                break;
            case (false, false, true):
                ObserveEmulatedPairingTransfer(transfer.Payload);
                break;
        }
    }

    void ObserveEmulatedL2capTransfer(ArmModemDspTransfer transfer)
    {
        if (_emulatedConnectionController is not null)
        {
            _emulatedConnectionController.ObserveDataChannelEstablished();
            CompleteEmulatedConnectionIfReady();
        }
        ObserveEmulatedL2capResponses(transfer);
    }

    void ObserveEmulatedL2capResponses(ArmModemDspTransfer transfer)
    {
        if (_emulatedL2capController is null)
        {
            return;
        }
        MiaBluetoothEmulationStatus previousStatus =
            CaptureEmulationStatus();
        foreach (byte[] response in _emulatedL2capController.ObserveOutbound(
                     transfer.Kind!.Value,
                     transfer.Payload))
        {
            QueueL2capResponse(response);
        }
        PublishEmulationStatusChangedIfChanged(previousStatus);
    }

    void QueueL2capResponse(byte[] response)
    {
        foreach ((byte kind, byte[] payload) in
                 BluetoothL2capController.FragmentInbound(response))
        {
            QueueDspInboundTransfer(0, kind, payload);
        }
    }

    void ObserveEmulatedConnectionTransfer(ReadOnlySpan<byte> payload)
    {
        BluetoothLegacyConnectionController controller =
            _emulatedConnectionController!;
        ScheduleControllerRecords(
            controller.ObserveOutbound(payload),
            countAsPairing: false);
        switch (controller.AuthenticationFailed, controller.IsComplete)
        {
            case (true, _):
                FailEmulatedConnection();
                break;
            case (false, true):
                CompleteEmulatedConnectionIfReady();
                break;
        }
    }

    void FailEmulatedConnection()
    {
        EmulatedConnectionAuthenticationFailureCount++;
        _emulatedConnectionController = null;
        _emulatedPairingPhase =
            ArmModemBluetoothPeripheralEmulatedPairingPhase.None;
        _suppressIdleTransfersForEmulatedTransaction = false;
        PublishEmulationStatusChanged();
    }

    void ObserveEmulatedPairingTransfer(ReadOnlySpan<byte> payload)
    {
        BluetoothLegacyPairingController controller =
            _emulatedPairingController!;
        ScheduleControllerRecords(
            controller.ObserveOutbound(payload),
            countAsPairing: true);
        switch (controller.AuthenticationFailed, controller.IsComplete)
        {
            case (true, _):
                FailEmulatedPairing();
                break;
            case (false, true):
                CompleteEmulatedPairing(controller);
                break;
        }
    }

    void FailEmulatedPairing()
    {
        if (!_emulatedPairingAuthenticationFailureCounted)
        {
            EmulatedPairingAuthenticationFailureCount++;
            _emulatedPairingAuthenticationFailureCounted = true;
            PublishEmulationStatusChanged();
        }
        _emulatedPairingController = null;
        _emulatedPairingPhase =
            ArmModemBluetoothPeripheralEmulatedPairingPhase.None;
        _suppressIdleTransfersForEmulatedTransaction = false;
    }

    void CompleteEmulatedPairing(BluetoothLegacyPairingController controller)
    {
        controller.LinkKey.CopyTo(_emulatedLinkKey);
        EmulatedPairingCompletedCount++;
        _emulatedPairingController = null;
        _emulatedPairingPhase =
            ArmModemBluetoothPeripheralEmulatedPairingPhase.None;
        _suppressIdleTransfersForEmulatedTransaction = false;
        PublishEmulationStatusChanged();
    }

    void ScheduleDspCompletion(bool isFixedTransfer)
    {
        _pendingDspOutputKickCycle = long.MaxValue;
        if (_modem is null)
        {
            CompleteDspTransferInline(isFixedTransfer);
            return;
        }

        // The diagnostic modem trace acknowledges each transaction two
        // 60,000-cycle DSP frames later. Model that asynchronous completion
        // rather than completing an MMIO transaction during its own writes.
        _pendingDspCompletions.Enqueue(
            isFixedTransfer,
            _modem.Cycles + 2 * DspFramePeriodCycles);
    }

    void CompleteDspTransferInline(bool isFixedTransfer)
    {
        _bus.AssertDspStatus(FixedTransferCompletionStatus);
        if (isFixedTransfer)
        {
            FixedTransferCompletionCount++;
        }
    }

    void OnLinkManagerActionDispatch(ArmModem modem)
    {
        if (!CanBeginEmulatedSecurityExchange(modem))
        {
            return;
        }
        if (EmulatedPairingCompletedCount == 0)
        {
            BeginEmulatedPairingControllerExchange();
        }
        else
        {
            BeginEmulatedConnectionControllerExchange();
        }
    }

    bool CanBeginEmulatedSecurityExchange(ArmModem modem) =>
        modem.Cpu.GetGpr(0) == PairingSetupAction &&
            _emulatedPairingController is null &&
            _emulatedConnectionController is null &&
            _emulatedPairingPhase is
                ArmModemBluetoothPeripheralEmulatedPairingPhase.KickDriver or
                ArmModemBluetoothPeripheralEmulatedPairingPhase.Result or
                ArmModemBluetoothPeripheralEmulatedPairingPhase.AwaitNativeAction;

    void OnDspCommandRingProducerWritten(ArmModemRamWrite write)
    {
        if (_disposed || write.Size < sizeof(ushort))
        {
            return;
        }

        // 010be4d2 copies C3EA's type, length, and payload into the slot at
        // the current producer index. It advances state+30 before returning
        // through 010be54e, so the just-committed slot is producer - 1.
        ushort nextProducer = BitConverter.ToUInt16(
            _bus.SnapshotExternal(
                ArmModemBus.ExternalRamBase + 0x0c40 + DspCommandRingProducerOffset,
                sizeof(ushort)));
        if (nextProducer >= DspCommandRingSlots)
        {
            throw new InvalidOperationException(
                $"BT_Ctrl DSP producer index {nextProducer} is outside its " +
                $"{DspCommandRingSlots}-slot ring.");
        }

        int committedSlot =
            (nextProducer + DspCommandRingSlots - 1) % DspCommandRingSlots;
        byte[] slot = _bus.SnapshotExternal(
            DspCommandRingBase + (uint)(committedSlot * DspCommandSlotLength),
            DspCommandSlotLength);
        int payloadLength = slot[1];
        if (payloadLength > DspCommandSlotLength - 2)
        {
            throw new InvalidOperationException(
                $"BT_Ctrl DSP command length {payloadLength} exceeds its " +
                $"{DspCommandSlotLength - 2}-byte ring slot.");
        }

        DspCommandCount++;
        var command = new ArmModemDspPacket(
            slot[0],
            slot.AsSpan(2, payloadLength).ToArray());
        DspCommandQueued?.Invoke(command);
        ObserveEmulatedPeerCommand(command);
        EnsureSuppressedDspOutputProgress();
    }

    void OnDspDriverStateWritten(ArmModemRamWrite write)
    {
        if (!_disposed &&
            (byte)write.Value == DspDriverResponseReadyState &&
            NextPeerServiceDueCycle() <= write.Cycle)
        {
            SchedulePeerService(write.Cycle + 1);
        }
    }

    void OnDspInterruptStatusChanged(byte status)
    {
        if (_disposed || status != 0)
        {
            return;
        }

        _emulatedIndexedEventInFlight = false;
        long due = NextPeerServiceDueCycle();
        if (due != long.MaxValue)
        {
            SchedulePeerService(Math.Max(_bus.Cycles + 1, due));
        }
    }

    void ObserveEmulatedPeerCommand(ArmModemDspPacket command)
    {
        CaptureEmulatedHandsetAddress(command);
        if (!EmulatePeer || _modem is null)
        {
            return;
        }

        ResumeIdleTransfersIfRemoteNameWasConsumed(command);
        if (TryBeginInquiry(command))
        {
            return;
        }

        if (command.Type != 0x48)
        {
            return;
        }

        _ = TryBeginRemoteNameResolution() ||
            TryBeginPairingSetup() ||
            TryBeginBondedConnectionSetup();
    }

    void CaptureEmulatedHandsetAddress(ArmModemDspPacket command)
    {
        if (command.Type != 0x28 || command.Payload.Length < 7)
        {
            return;
        }

        // The final seven bytes use the same bit packing as inquiry
        // addresses. The boot trace carries 00-D2-0C-EC-01-00 here,
        // which native E21/E1 later use as the handset address.
        DecodeInquiryAddress(
            command.Payload.AsSpan(command.Payload.Length - 7),
            _emulatedHandsetAddress);
    }

    void ResumeIdleTransfersIfRemoteNameWasConsumed(ArmModemDspPacket command)
    {
        if (_resumeIdleTransfersAfterRemoteNameCommand &&
            command.Type == 0x1a &&
            command.Payload.AsSpan().SequenceEqual(new byte[] { 0x80 }))
        {
            // The controller emits this receiver-enable command only after
            // it has consumed the serialized kind-3 name record through
            // C40C. Resuming the normal empty DSP frame cadence any earlier
            // lets a kind-0 notification overtake C40C and corrupts D2FF.
            _resumeIdleTransfersAfterRemoteNameCommand = false;
            _suppressIdleTransfersForEmulatedTransaction = false;
        }
    }

    bool TryBeginInquiry(ArmModemDspPacket command)
    {
        if (command.Type != 0x18 ||
            !command.Payload.AsSpan().SequenceEqual(new byte[] { 0x18, 0x18 }))
        {
            return false;
        }

        // Inquiry setup spans several native timers and packet-port
        // transactions. The measured controller response begins after
        // that setup window with a combined indexed-event/empty transfer,
        // which moves the DSP driver from state 3 to state 1.
        _emulatedInquiryPhase = ArmModemBluetoothPeripheralEmulatedInquiryPhase.KickDriver;
        _emulatedInquiryDueCycle = _modem!.Cycles + EmulatedInquirySettleCycles;
        SchedulePeerService(_emulatedInquiryDueCycle);
        _suppressIdleTransfersForEmulatedTransaction = true;
        return true;
    }

    bool TryBeginRemoteNameResolution()
    {
        if (_emulatedRemoteNameResolved)
        {
            return false;
        }

        // The first type-48 transaction after inquiry resolves the one
        // emulated peer's name. Pairing later reuses type 48 with a
        // different controller transaction; replaying C40C there is
        // rejected by the link manager's state-08 table and can overtake
        // the pairing-specific result.
        _emulatedRemoteNameRecordPending = true;
        _emulatedRemoteNameRecordDueCycle =
            _modem!.Cycles + EmulatedNameRecordDelayCycles;
        SchedulePeerService(_emulatedRemoteNameRecordDueCycle);
        return true;
    }

    bool TryBeginPairingSetup()
    {
        if (EmulatedPairingCompletedCount != 0 ||
            _emulatedPairingPhase != ArmModemBluetoothPeripheralEmulatedPairingPhase.None)
        {
            return false;
        }

        // Runtime link-manager state 08 accepts the indexed C3E5 event
        // before it advances to the pairing-result states. The DSP
        // driver constructs C3E5 itself from status 15 and its indexed
        // register bank, so keep this at the controller boundary.
        BeginSecuritySetupAfter(EmulatedPairingSetupDelayCycles);
        return true;
    }

    bool TryBeginBondedConnectionSetup()
    {
        if (EmulatedPairingCompletedCount == 0 ||
            !_emulatedBondedConnectionArmed ||
            _emulatedConnectionController is not null ||
            _emulatedPairingPhase != ArmModemBluetoothPeripheralEmulatedPairingPhase.None)
        {
            return false;
        }

        // Selecting a bonded peer for transfer enters the same native
        // status-15 link setup, but authentication uses the stored link
        // key and must not re-enter PIN/key derivation.
        BeginSecuritySetupAfter(EmulatedConnectionSetupDelayCycles);
        _emulatedBondedConnectionArmed = false;
        return true;
    }

    void BeginSecuritySetupAfter(long delayCycles)
    {
        _emulatedPairingPhase = ArmModemBluetoothPeripheralEmulatedPairingPhase.KickDriver;
        _emulatedPairingSetupDueCycle = _modem!.Cycles + delayCycles;
        SchedulePeerService(_emulatedPairingSetupDueCycle);
        _suppressIdleTransfersForEmulatedTransaction = true;
    }

    void ServiceEmulatedPeer()
    {
        if (!EmulatePeer ||
            (_bus.IrqPending & ArmModemBus.DspInterruptBit) != 0)
        {
            return;
        }

        if (TryServiceDueControllerRecord() ||
            TryStartInquiryDriverExchange() ||
            TryCompleteInquiryDriverExchange() ||
            TryStartPairingDriverExchange() ||
            TryCompletePairingDriverExchange())
        {
            return;
        }

        ServiceRemoteNameRecordIfDue();
    }

    bool TryServiceDueControllerRecord()
    {
        if (!_pendingPairingControllerRecords.TryPeek(
                out byte[]? pairingRecord,
                out long dueCycle) ||
            _bus.Cycles < dueCycle)
        {
            return false;
        }

        _pendingPairingControllerRecords.Dequeue();
        QueueControllerRecord(pairingRecord);
        if (_emulatedConnectionController is not null)
        {
            EmulatedConnectionControllerRecordCount++;
        }
        else
        {
            EmulatedPairingControllerRecordCount++;
        }
        return true;
    }

    bool TryStartInquiryDriverExchange()
    {
        if (_emulatedInquiryPhase != ArmModemBluetoothPeripheralEmulatedInquiryPhase.KickDriver ||
            _bus.Cycles < _emulatedInquiryDueCycle)
        {
            return false;
        }

        if (ReadDspDriverState() == DspDriverResponseReadyState)
        {
            CompleteInquiryDriverExchange();
            return true;
        }

        KickIndexedDriver();
        _emulatedInquiryPhase = ArmModemBluetoothPeripheralEmulatedInquiryPhase.Result;
        _emulatedInquiryDueCycle = _bus.Cycles + EmulatedResponseDelayCycles;
        SchedulePeerService(_emulatedInquiryDueCycle);
        return true;
    }

    bool TryCompleteInquiryDriverExchange()
    {
        if (_emulatedInquiryPhase != ArmModemBluetoothPeripheralEmulatedInquiryPhase.Result ||
            _bus.Cycles < _emulatedInquiryDueCycle ||
            ReadDspDriverState() != DspDriverResponseReadyState)
        {
            return false;
        }

        CompleteInquiryDriverExchange();
        return true;
    }

    void CompleteInquiryDriverExchange()
    {
        AssertEmulatedIndexedStatus();
        _emulatedInquiryPhase = ArmModemBluetoothPeripheralEmulatedInquiryPhase.None;
        _emulatedInquiryDueCycle = long.MaxValue;
        CompleteEmulatedInquiry();
    }

    bool TryStartPairingDriverExchange()
    {
        if (_emulatedPairingPhase != ArmModemBluetoothPeripheralEmulatedPairingPhase.KickDriver ||
            _bus.Cycles < _emulatedPairingSetupDueCycle)
        {
            return false;
        }

        if (ReadDspDriverState() == DspDriverResponseReadyState)
        {
            CompleteEmulatedPairingSetup();
            return true;
        }

        KickIndexedDriver();
        _emulatedPairingPhase = ArmModemBluetoothPeripheralEmulatedPairingPhase.Result;
        _emulatedPairingSetupDueCycle = _bus.Cycles + EmulatedResponseDelayCycles;
        SchedulePeerService(_emulatedPairingSetupDueCycle);
        return true;
    }

    bool TryCompletePairingDriverExchange()
    {
        if (_emulatedPairingPhase != ArmModemBluetoothPeripheralEmulatedPairingPhase.Result ||
            _bus.Cycles < _emulatedPairingSetupDueCycle ||
            ReadDspDriverState() != DspDriverResponseReadyState)
        {
            return false;
        }

        CompleteEmulatedPairingSetup();
        return true;
    }

    void KickIndexedDriver()
    {
        ConfigureEmulatedInquiryRegisters();
        if (_bus.DspInboundTransferByteCount == 0)
        {
            _bus.QueueDspInboundTransfer(0, 0, []);
        }
        _emulatedIndexedEventInFlight = true;
        _bus.AssertDspStatus(0x15);
    }

    void ServiceRemoteNameRecordIfDue()
    {
        if (!_emulatedRemoteNameRecordPending ||
            _bus.Cycles < _emulatedRemoteNameRecordDueCycle)
        {
            return;
        }

        QueueControllerRecord(CreateEmulatedRemoteNameRecord());
        _emulatedRemoteNameRecordPending = false;
        _emulatedRemoteNameRecordDueCycle = long.MaxValue;
        _resumeIdleTransfersAfterRemoteNameCommand = true;
        _emulatedRemoteNameResolved = true;
        EmulatedRemoteNameResponseCount++;
        PublishEmulationStatusChanged();
    }

    void SchedulePeerService(long cycle)
    {
        if (_disposed || _modem is null || cycle >= _peerServiceCycle)
        {
            return;
        }

        _peerServiceEvent?.Dispose();
        _peerServiceCycle = cycle;
        _peerServiceEvent = _bus.SchedulePeripheralEvent(
            cycle,
            _ =>
            {
                _peerServiceEvent = null;
                _peerServiceCycle = long.MaxValue;
                if (!_disposed)
                {
                    ServiceEmulatedPeer();
                }
            });
    }

    long NextPeerServiceDueCycle()
    {
        long recordDue = _pendingPairingControllerRecords.TryPeek(
            out _,
            out long recordDueCycle)
                ? recordDueCycle
                : long.MaxValue;
        long inquiryDue = _emulatedInquiryPhase is
            ArmModemBluetoothPeripheralEmulatedInquiryPhase.KickDriver or
            ArmModemBluetoothPeripheralEmulatedInquiryPhase.Result
                ? _emulatedInquiryDueCycle
                : long.MaxValue;
        long pairingDue = _emulatedPairingPhase is
            ArmModemBluetoothPeripheralEmulatedPairingPhase.KickDriver or
            ArmModemBluetoothPeripheralEmulatedPairingPhase.Result
                ? _emulatedPairingSetupDueCycle
                : long.MaxValue;
        long remoteNameDue = _emulatedRemoteNameRecordPending
            ? _emulatedRemoteNameRecordDueCycle
            : long.MaxValue;
        return Math.Min(
            Math.Min(recordDue, inquiryDue),
            Math.Min(pairingDue, remoteNameDue));
    }

    byte ReadDspDriverState() =>
        _bus.SnapshotExternal(DspDriverStateAddress, 1)[0];

    void CompleteEmulatedInquiry()
    {
        EmulatedInquiryResponseCount++;
        if (EmulatedPairingCompletedCount != 0)
        {
            _emulatedBondedConnectionArmed = true;
        }
        PublishEmulationStatusChanged();
    }

    void EnsureSuppressedDspOutputProgress()
    {
        if (EmulatedPairingCompletedCount == 0 ||
            !_suppressIdleTransfersForEmulatedTransaction ||
            _pendingDspOutputKickCycle != long.MaxValue ||
            _pendingDspCompletions.Count != 0 ||
            (_bus.DspInterruptStatus & FixedTransferCompletionStatus) != 0 ||
            !HasPendingDspOutput())
        {
            return;
        }

        // Suppressing kind-0 inbound frames must not suppress the independent
        // DSP packet-port ready cadence. When BT_Ctrl changes an empty output
        // ring to nonempty during such a transaction, provide one measured
        // status-08 edge two frames later. The packet emitted from that edge
        // schedules the next edge through ScheduleDspCompletion, so this is a
        // transport kick rather than a firmware-state or queue-pointer patch.
        _pendingDspOutputKickCycle =
            _modem!.Cycles + 2 * DspFramePeriodCycles;
    }

    bool HasPendingDspOutput()
    {
        byte[] indexes = _bus.SnapshotExternal(
            ArmModemBus.ExternalRamBase + 0x0c40 +
                DspCommandRingProducerOffset,
            4);
        return BitConverter.ToUInt16(indexes, 0) !=
            BitConverter.ToUInt16(indexes, 2);
    }

    void CompleteEmulatedPairingSetup()
    {
        AssertEmulatedIndexedStatus();
        _emulatedPairingSetupDueCycle = long.MaxValue;
        _emulatedPairingPhase = ArmModemBluetoothPeripheralEmulatedPairingPhase.AwaitNativeAction;
    }

    void BeginEmulatedPairingControllerExchange()
    {
        EmulatedPairingSetupResponseCount++;
        _emulatedPairingAuthenticationFailureCounted = false;

        byte[] pin = _emulatedPeerPin
            .Select(character => checked((byte)character))
            .ToArray();
        _emulatedPairingController = new BluetoothLegacyPairingController(
            EmulatedPeerAddressBytes,
            _emulatedHandsetAddress,
            pin);
        SchedulePairingControllerRecords(
            [_emulatedPairingController.Begin()]);
        _emulatedPairingPhase = ArmModemBluetoothPeripheralEmulatedPairingPhase.ControllerExchange;
    }

    void BeginEmulatedConnectionControllerExchange()
    {
        EmulatedConnectionSetupResponseCount++;
        _emulatedConnectionController = new BluetoothLegacyConnectionController(
            EmulatedPeerAddressBytes,
            _emulatedHandsetAddress,
            _emulatedLinkKey);
        _emulatedL2capController = new BluetoothL2capController(
            initiateIncomingObjectPush: EmulateIncomingObjectPush,
            incomingObject: _emulatedIncomingObject);
        ScheduleControllerRecords(
            _emulatedConnectionController.Begin(),
            countAsPairing: false);
        _emulatedPairingPhase = ArmModemBluetoothPeripheralEmulatedPairingPhase.ControllerExchange;
    }

    void SchedulePairingControllerRecords(IReadOnlyList<byte[]> records) =>
        ScheduleControllerRecords(records, countAsPairing: true);

    void ScheduleControllerRecords(
        IReadOnlyList<byte[]> records,
        bool countAsPairing)
    {
        if (_modem is null)
        {
            return;
        }

        for (var index = 0; index < records.Count; index++)
        {
            long dueCycle =
                _modem.Cycles +
                EmulatedResponseDelayCycles +
                index * EmulatedPairingRecordSpacingCycles;
            _pendingPairingControllerRecords.Enqueue(records[index], dueCycle);
            SchedulePeerService(dueCycle);
        }
    }

    void AssertEmulatedIndexedStatus()
    {
        ConfigureEmulatedInquiryRegisters();
        _emulatedIndexedEventInFlight = true;
        _bus.AssertDspStatus(0x15);
    }

    void ConfigureEmulatedInquiryRegisters()
    {
        // Recovered status-15 indexed bank used by 010be13c to construct
        // genuine C3E5. Action 5 calls 010abfe0 to unpack indexes 4..10
        // into a six-byte Bluetooth address before publishing D2FA.
        Span<byte> values = stackalloc byte[19];
        EncodeInquiryAddress(
            [0x11, 0x22, 0x33, 0x44, 0x55, 0x66],
            values[4..11]);
        values[11] = 0x0c;
        values[12] = 0x02;
        values[13] = 0x5a;
        values[14] = 0x0c;
        values[15] = 0x0d;
        values[16] = DefaultMeasurement;
        values[17] = DefaultMeasurement;
        values[18] = 0x0d;
        for (var address = 0; address < values.Length; address++)
        {
            _bus.SetDspIndexedRegister((byte)address, values[address]);
        }
        _bus.SetDspIndexedReadAddress(0);
    }

    internal static void EncodeInquiryAddress(
        ReadOnlySpan<byte> address,
        Span<byte> packed)
    {
        if (address.Length != 6)
        {
            throw new ArgumentException(
                "An emulated Bluetooth address must contain six bytes.",
                nameof(address));
        }
        if (packed.Length != 7)
        {
            throw new ArgumentException(
                "The native inquiry address field must contain seven bytes.",
                nameof(packed));
        }

        packed[0] = (byte)(address[0] << 2);
        packed[1] = (byte)((address[0] >> 6) | (address[1] << 2));
        packed[2] = (byte)((address[1] >> 6) | (address[2] << 2));
        packed[3] = (byte)(address[2] >> 6);
        address[3..].CopyTo(packed[4..]);
    }

    internal static void DecodeInquiryAddress(
        ReadOnlySpan<byte> packed,
        Span<byte> address)
    {
        if (packed.Length != 7)
        {
            throw new ArgumentException(
                "The native inquiry address field must contain seven bytes.",
                nameof(packed));
        }
        if (address.Length != 6)
        {
            throw new ArgumentException(
                "A decoded Bluetooth address must contain six bytes.",
                nameof(address));
        }

        address[0] = (byte)((packed[0] >> 2) | (packed[1] << 6));
        address[1] = (byte)((packed[1] >> 2) | (packed[2] << 6));
        address[2] = (byte)((packed[2] >> 2) | (packed[3] << 6));
        packed[4..].CopyTo(address[3..]);
    }

    static byte[] CreateEmulatedRemoteNameRecord()
    {
        ReadOnlySpan<byte> name = "Mia Peer"u8;
        var record = new byte[ControllerRecordLength];
        // Kind-3 selector byte 04 decodes as record ID 2 / C40C.
        // C40C+3 is the chunk offset, +4 is total length, and +5..+18
        // contain the fourteen-byte chunk copied from record[3..16].
        record[0] = 0x04;
        record[1] = 0;
        record[2] = checked((byte)name.Length);
        name.CopyTo(record.AsSpan(3));
        return record;
    }

    void OnMmioAccessed(ArmModemMmioAccess access)
    {
        // 010bdfc4 writes 17 while BT_Ctrl resets the DSP interface. The
        // physical DSP starts its 60,000-cycle frame source from that edge.
        if (!access.IsWrite || access.Address != DspStatusAddress ||
            access.Size != 1 || (byte)access.Value != DspResetStatusMask)
        {
            return;
        }

        _dspFrameNumber = 0;
        _nextDspFrameCycle = access.Cycle + DspFramePeriodCycles;
        _bus.ScheduleExternalDspFrame(_nextDspFrameCycle);
    }

    void InitializeIndexedRegisters()
    {
        // BT_Ctrl samples this complete bank during the first frame IRQ. The
        // diagnostic trace that reaches native packet emission initializes all
        // 256 entries to zero and supplies the two measured 0xd0 values.
        for (var address = 0; address <= byte.MaxValue; address++)
        {
            _bus.SetDspIndexedRegister((byte)address, 0);
        }
        _bus.SetDspIndexedRegister(15, DefaultMeasurement);
        _bus.SetDspIndexedRegister(16, DefaultMeasurement);
    }

    void OnExternalDspFrameDue(long cycle)
    {
        if (_disposed || cycle != _nextDspFrameCycle)
        {
            return;
        }

        // Status 08 is a level-sensitive transaction completion, so one
        // assertion can retire only one output-ring entry. Several packet
        // writes commonly become due in the same DSP frame; asserting them
        // in one loop collapses those completions into a single status bit
        // and leaves BT_Ctrl's producer/consumer counters permanently
        // unequal. Keep each completion queued until the firmware has
        // acknowledged the preceding DSP interrupt, then expose one new
        // edge per frame.
        CompleteDueDspOutput(cycle);

        // Registers 4..7 carry the running DSP frame counter. This packing
        // and cadence are measured from the firmware boot path.
        UpdateDspFrameCounter();

        // A real inbound transfer has priority over an idle frame. Releases
        // are serialized through the same FIFO so controller records and
        // L2CAP packets cannot overwrite the firmware's current transfer.
        if (TryDeliverDspInboundTransfer())
        {
            ScheduleNextDspFrame(cycle);
            return;
        }

        // The observed idle frame is an empty native kind-0 transfer. Never
        // append to an unconsumed transfer: the hardware cannot replace a
        // frame while BT_Ctrl still owns it, and queueing did so made the
        // synthetic DSP permanently outrun the firmware during discovery.
        QueueIdleDspFrameIfAvailable();
        ScheduleNextDspFrame(cycle);
    }

    void CompleteDueDspOutput(long cycle)
    {
        if ((_bus.DspInterruptStatus & FixedTransferCompletionStatus) != 0)
        {
            return;
        }

        if (TryCompleteFixedTransfer(cycle))
        {
            return;
        }

        CompletePendingOutputKick(cycle);
    }

    bool TryCompleteFixedTransfer(long cycle)
    {
        if (!_pendingDspCompletions.TryPeek(
                out bool fixedTransfer,
                out long due) ||
            due > cycle)
        {
            return false;
        }

        _pendingDspCompletions.Dequeue();
        _bus.AssertDspStatus(FixedTransferCompletionStatus);
        if (fixedTransfer)
        {
            FixedTransferCompletionCount++;
        }
        return true;
    }

    void CompletePendingOutputKick(long cycle)
    {
        if (_pendingDspOutputKickCycle > cycle)
        {
            return;
        }

        _pendingDspOutputKickCycle = long.MaxValue;
        if (HasPendingDspOutput())
        {
            _bus.AssertDspStatus(FixedTransferCompletionStatus);
        }
    }

    void UpdateDspFrameCounter()
    {
        int frame = _dspFrameNumber++;
        if (_emulatedIndexedEventInFlight)
        {
            return;
        }

        byte low = (byte)frame;
        byte middle = (byte)(frame >> 8);
        byte high = (byte)(frame >> 16);
        _bus.SetDspIndexedRegister(4, (byte)(low << 2));
        _bus.SetDspIndexedRegister(5, (byte)((low >> 6) | (middle << 2)));
        _bus.SetDspIndexedRegister(6, (byte)((middle >> 6) | (high << 2)));
        _bus.SetDspIndexedRegister(7, (byte)(high >> 6));
    }

    void QueueIdleDspFrameIfAvailable()
    {
        if (!EmitIdleTransferFrames ||
            _suppressIdleTransfersForEmulatedTransaction ||
            _bus.DspInboundTransferByteCount != 0)
        {
            return;
        }

        _bus.QueueDspInboundTransfer(0, 0, []);
    }

    void ScheduleNextDspFrame(long cycle)
    {
        _nextDspFrameCycle = cycle + DspFramePeriodCycles;
        _bus.ScheduleExternalDspFrame(_nextDspFrameCycle);
    }

    void QueueDspInboundTransfer(
        byte control,
        byte kind,
        ReadOnlySpan<byte> payload)
    {
        if (_modem is null)
        {
            _bus.QueueDspInboundTransfer(control, kind, payload);
            return;
        }

        _pendingDspInboundTransfers.Enqueue(
            new ArmModemBluetoothPeripheralPendingDspInboundTransfer(
                control,
                kind,
                payload.ToArray()));
        TryDeliverDspInboundTransfer();
    }

    bool TryDeliverDspInboundTransfer()
    {
        if (_bus.DspInboundTransferByteCount != 0 ||
            !_pendingDspInboundTransfers.TryDequeue(
                out ArmModemBluetoothPeripheralPendingDspInboundTransfer transfer))
        {
            return false;
        }

        _bus.QueueDspInboundTransfer(
            transfer.Control,
            transfer.Kind,
            transfer.Payload);
        return true;
    }

    void CompleteEmulatedConnectionIfReady()
    {
        if (_emulatedConnectionController is null ||
            !_emulatedConnectionController.IsComplete)
        {
            return;
        }

        EmulatedConnectionCompletedCount++;
        _emulatedConnectionController = null;
        _emulatedPairingPhase = ArmModemBluetoothPeripheralEmulatedPairingPhase.None;
        PublishEmulationStatusChanged();
        // A native L2CAP request proves that baseband setup completed, but it
        // does not end the peer transaction. Keep the idle-transfer stream
        // suppressed while the data channel is active so
        // EnsureSuppressedDspOutputProgress can provide the independent
        // status-08 edge for each later SDP/RFCOMM/OBEX packet committed by
        // the handset. Clearing this here strands the first post-connect SDP
        // packet in BT_Ctrl's output ring.
    }

    void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    void PublishEmulationStatusChanged() =>
        EmulationStatusChanged?.Invoke(CaptureEmulationStatus());

    void PublishEmulationStatusChangedIfChanged(
        MiaBluetoothEmulationStatus previousStatus)
    {
        var status = CaptureEmulationStatus();
        if (status != previousStatus)
        {
            EmulationStatusChanged?.Invoke(status);
        }
    }
}
