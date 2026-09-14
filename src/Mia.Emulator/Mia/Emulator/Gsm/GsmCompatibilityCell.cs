// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

/// <summary>
/// Functional GSM cell facade at the decoded hardware boundaries. It does not
/// model RF waveforms or the original private equalizer ABI; instead, a tuned
/// and sufficiently strong configured carrier produces deterministic FCH,
/// SCH, and BCCH results through the existing firmware-visible devices.
/// </summary>
internal sealed class GsmCompatibilityCell :
    IAsicFchSource,
    IAsicEqualizerSource,
    IAsicChannelDecoderSource,
    IAsicChannelEncoderSink
{
    public const ushort MinimumRawSample = 0x0800;
    public const ushort AcceptedFchMetric = 0x2ee1;
    public const byte DefaultBsic = 0;
    public const int DefaultSchFrameNumber = 1;
    public const byte DefaultMsTxPowerMaxCch = 7;
    public const byte RandomAccessUplinkEncoderState = 2;
    public const byte IdlePagingDecoderState = 4;
    public const byte CellAcquisitionDecoderState = 5;
    public const byte DedicatedDownlinkDecoderState = 7;
    public const byte DedicatedSacchDecoderState = 6;
    public const byte AssignedSignallingDownlinkDecoderState = 13;
    public const byte DedicatedUplinkEncoderState = 9;
    public const byte TrafficUplinkEncoderState = 10;
    const int MaximumPagingAttempts = 8;

    readonly byte[] _locationAreaIdentity;
    readonly Func<GsmRandomAccessReference?>? _randomAccessReferenceSource;
    readonly Func<bool>? _incomingPagingReadySource;
    readonly Action<GsmOutgoingNetworkRequest>? _outgoingNetworkRequest;
    readonly Action<GsmDedicatedUplinkFrame>? _dedicatedUplinkFrameObserved;
    readonly Action<byte>? _controlChannelDecodeObserved;
    readonly Func<DateTimeOffset> _networkTimeProvider;
    readonly MiaWorker? _worker;
    readonly Queue<GsmCompatibilityCellDedicatedDownlinkFrame> _dedicatedDownlinkFrames = [];
    readonly Queue<GsmCompatibilityCellGsmIncomingSms> _incomingSms = [];
    readonly Queue<GsmCompatibilityCellGsmIncomingCall> _incomingCalls = [];
    readonly byte[] _nextUplinkReceiveSequences = new byte[8];
    readonly byte[] _nextDownlinkSendSequences = new byte[8];
    readonly List<byte>[] _uplinkReassembly =
    [
        new(), new(), new(), new(), new(), new(), new(), new(),
    ];
    readonly long[] _controlChannelStateCounts = new long[byte.MaxValue + 1];
    byte? _pendingRandomAccessRequestReference;
    GsmPendingOutgoingNetworkRequest? _pendingOutgoingRequest;
    GsmCompatibilityCellGsmIncomingSms? _activeIncomingSms;
    GsmCompatibilityCellGsmIncomingCall? _activeIncomingCall;
    byte _activeIncomingSmsMessageReference;
    byte _nextIncomingSmsMessageReference = 0x40;
    int _incomingPagingAttemptCount;
    int _controlChannelIndex = -1;
    int _cellAcquisitionIndex;
    int _dedicatedSacchIndex = -1;
    byte _lastControlChannelFirmwareState = byte.MaxValue;
    long _fchAttemptCount;
    long _fchSuccessCount;
    long _schDecodeCount;
    long _controlChannelDecodeCount;
    long _randomAccessRequestCount;
    long _immediateAssignmentCount;
    long _dedicatedUplinkCount;
    long _sabmCount;
    long _uaCount;
    long _locationUpdatingAcceptCount;
    long _receiveReadyCount;
    long _channelReleaseCount;
    long _cmServiceRequestCount;
    long _cipheringModeCommandCount;
    long _cipheringModeCompleteCount;
    long _dedicatedUplinkInformationCount;
    long _smsCpDataCount;
    long _smsRpDataCount;
    long _smsRpSmmaCount;
    long _smsCpAckCount;
    long _smsRpAckCount;
    long _smsRpErrorCount;
    long _outgoingSmsRequestCount;
    long _outgoingCallRequestCount;
    long _callProceedingCount;
    long _callAlertingCount;
    long _callConnectCount;
    long _mobileSmsCpAckCount;
    long _mobileDedicatedReleaseCount;
    long _queuedIncomingSmsCount;
    long _queuedIncomingCallCount;
    long _pagingRequestCount;
    long _pagingResponseCount;
    long _mobileTerminatedSapi3EstablishmentCount;
    long _mobileTerminatedSmsCount;
    long _mobileTerminatedSmsCpAckCount;
    long _mobileTerminatedSmsRpAckCount;
    long _deliveredIncomingSmsCount;
    long _mobileTerminatedCallSetupCount;
    long _mobileTerminatedCallConfirmedCount;
    long _mobileTerminatedCallConnectCount;
    long _mobileTerminatedCallDisconnectCount;
    long _mobileTerminatedCallReleaseCompleteCount;
    long _mobileTerminatedTrafficAssignmentCompleteCount;
    long _mobileTerminatedTrafficAssignmentCount;
    long _mobileTerminatedTrafficAssignmentDeliveredCount;
    bool _dedicatedLinkActive;
    bool _awaitingLocationUpdatingAcceptAcknowledgement;
    bool _awaitingCipheringModeComplete;
    bool _channelReleaseQueued;
    bool _channelReleaseDelivered;
    bool _mmConnectionActive;
    bool _smsTransactionComplete;
    bool _registered;
    bool _incomingPagingAnswered;
    bool _incomingPageOutstanding;
    bool _incomingSmsTransactionActive;
    bool _incomingCallTransactionActive;
    bool _incomingCallReleaseComplete;
    bool _mobileTerminatedTrafficAssignmentInProgress;
    bool _assignedSignallingChannelActive;
    bool _awaitingIncomingSapi3Ua;
    bool _awaitingCallProceedingAcknowledgement;
    bool _awaitingCallAlertingAcknowledgement;
    byte _activeCallSapi;
    byte _activeCallTransactionAndProtocolDiscriminator;

    public GsmCompatibilityCell(
        short arfcn,
        ushort rawSample,
        string imsi = SwSimCard.DefaultImsi,
        byte bsic = DefaultBsic,
        Func<GsmRandomAccessReference?>? randomAccessReferenceSource = null,
        Action<GsmOutgoingNetworkRequest>? outgoingNetworkRequest = null,
        Func<DateTimeOffset>? networkTimeProvider = null,
        Func<bool>? incomingPagingReadySource = null,
        Action<GsmDedicatedUplinkFrame>? dedicatedUplinkFrameObserved = null,
        Action<byte>? controlChannelDecodeObserved = null,
        MiaWorker? worker = null)
    {
        Arfcn = arfcn is >= AsicRfFrontend.BandZeroFirstArfcn and
            <= AsicRfFrontend.BandZeroLastArfcn
            ? arfcn
            : throw new ArgumentOutOfRangeException(nameof(arfcn));
        RawSample = rawSample;
        Imsi = ValidateImsi(imsi);
        Bsic = bsic <= 0x3f
            ? bsic
            : throw new ArgumentOutOfRangeException(nameof(bsic));
        _locationAreaIdentity = EncodeLocationAreaIdentity(Imsi, lac: 1);
        _randomAccessReferenceSource = randomAccessReferenceSource;
        _incomingPagingReadySource = incomingPagingReadySource;
        _outgoingNetworkRequest = outgoingNetworkRequest;
        _dedicatedUplinkFrameObserved = dedicatedUplinkFrameObserved;
        _controlChannelDecodeObserved = controlChannelDecodeObserved;
        _networkTimeProvider = networkTimeProvider ?? (() => DateTimeOffset.Now);
        _worker = worker;
    }

    public short Arfcn { get; }

    public ushort RawSample { get; }

    public string Imsi { get; }

    public byte Bsic { get; }

    public AsicEqualizerResult Equalize(AsicEqualizerRequest request)
    {
        if (RawSample < MinimumRawSample)
        {
            return AsicEqualizerResult.Invalid;
        }

        // A present compatibility carrier completes the native equalizer
        // transaction with neutral selectors. The private selector units are
        // still unknown; zero is the documented compatibility identity and
        // keeps signal strength on the independently proven RF/ADC path.
        return default;
    }

    public long FchAttemptCount => Interlocked.Read(ref _fchAttemptCount);

    public long FchSuccessCount => Interlocked.Read(ref _fchSuccessCount);

    public long SchDecodeCount => Interlocked.Read(ref _schDecodeCount);

    public long ControlChannelDecodeCount =>
        Interlocked.Read(ref _controlChannelDecodeCount);

    public long GetControlChannelStateCount(byte firmwareState) =>
        Interlocked.Read(ref _controlChannelStateCounts[firmwareState]);

    public long ImmediateAssignmentCount =>
        Interlocked.Read(ref _immediateAssignmentCount);

    public long RandomAccessRequestCount =>
        Interlocked.Read(ref _randomAccessRequestCount);

    public long DedicatedUplinkCount =>
        Interlocked.Read(ref _dedicatedUplinkCount);

    public long SabmCount => Interlocked.Read(ref _sabmCount);

    public long UaCount => Interlocked.Read(ref _uaCount);

    public long LocationUpdatingAcceptCount =>
        Interlocked.Read(ref _locationUpdatingAcceptCount);

    public long ReceiveReadyCount => Interlocked.Read(ref _receiveReadyCount);

    public long ChannelReleaseCount =>
        Interlocked.Read(ref _channelReleaseCount);

    public long CmServiceRequestCount =>
        Interlocked.Read(ref _cmServiceRequestCount);

    public long CipheringModeCommandCount =>
        Interlocked.Read(ref _cipheringModeCommandCount);

    public long CipheringModeCompleteCount =>
        Interlocked.Read(ref _cipheringModeCompleteCount);

    public long DedicatedUplinkInformationCount =>
        Interlocked.Read(ref _dedicatedUplinkInformationCount);

    public long SmsCpDataCount => Interlocked.Read(ref _smsCpDataCount);

    public long SmsRpDataCount => Interlocked.Read(ref _smsRpDataCount);

    public long SmsRpSmmaCount => Interlocked.Read(ref _smsRpSmmaCount);

    public long SmsCpAckCount => Interlocked.Read(ref _smsCpAckCount);

    public long SmsRpAckCount => Interlocked.Read(ref _smsRpAckCount);

    public long SmsRpErrorCount => Interlocked.Read(ref _smsRpErrorCount);

    public long OutgoingSmsRequestCount =>
        Interlocked.Read(ref _outgoingSmsRequestCount);

    public long OutgoingCallRequestCount =>
        Interlocked.Read(ref _outgoingCallRequestCount);

    public long CallProceedingCount =>
        Interlocked.Read(ref _callProceedingCount);

    public long CallAlertingCount =>
        Interlocked.Read(ref _callAlertingCount);

    public long CallConnectCount => Interlocked.Read(ref _callConnectCount);

    public long MobileSmsCpAckCount =>
        Interlocked.Read(ref _mobileSmsCpAckCount);

    public long MobileDedicatedReleaseCount =>
        Interlocked.Read(ref _mobileDedicatedReleaseCount);

    public long QueuedIncomingSmsCount =>
        Interlocked.Read(ref _queuedIncomingSmsCount);

    public long QueuedIncomingCallCount =>
        Interlocked.Read(ref _queuedIncomingCallCount);

    public long PagingRequestCount => Interlocked.Read(ref _pagingRequestCount);

    public long PagingResponseCount => Interlocked.Read(ref _pagingResponseCount);

    public long MobileTerminatedSapi3EstablishmentCount =>
        Interlocked.Read(ref _mobileTerminatedSapi3EstablishmentCount);

    public long MobileTerminatedSmsCount =>
        Interlocked.Read(ref _mobileTerminatedSmsCount);

    public long MobileTerminatedSmsCpAckCount =>
        Interlocked.Read(ref _mobileTerminatedSmsCpAckCount);

    public long MobileTerminatedSmsRpAckCount =>
        Interlocked.Read(ref _mobileTerminatedSmsRpAckCount);

    public long DeliveredIncomingSmsCount =>
        Interlocked.Read(ref _deliveredIncomingSmsCount);

    public long MobileTerminatedCallSetupCount =>
        Interlocked.Read(ref _mobileTerminatedCallSetupCount);

    public long MobileTerminatedCallConfirmedCount =>
        Interlocked.Read(ref _mobileTerminatedCallConfirmedCount);

    public long MobileTerminatedCallConnectCount =>
        Interlocked.Read(ref _mobileTerminatedCallConnectCount);

    public long MobileTerminatedCallDisconnectCount =>
        Interlocked.Read(ref _mobileTerminatedCallDisconnectCount);

    public long MobileTerminatedCallReleaseCompleteCount =>
        Interlocked.Read(ref _mobileTerminatedCallReleaseCompleteCount);

    public long MobileTerminatedTrafficAssignmentCount =>
        Interlocked.Read(ref _mobileTerminatedTrafficAssignmentCount);

    public long MobileTerminatedTrafficAssignmentDeliveredCount =>
        Interlocked.Read(ref _mobileTerminatedTrafficAssignmentDeliveredCount);

    public long MobileTerminatedTrafficAssignmentCompleteCount =>
        Interlocked.Read(ref _mobileTerminatedTrafficAssignmentCompleteCount);

    // Single-bool state latches: safe to publish with Volatile rather than
    // routing through the owner worker, matching CommandStatus-style scalar
    // fields elsewhere in this codebase. Advance() polls Registered on every
    // work-item batch, so a full cross-thread Invoke round trip here would
    // be pure overhead.
    public bool MmConnectionActive => Volatile.Read(ref _mmConnectionActive);

    public bool Registered => Volatile.Read(ref _registered);

    public bool ResolveNetworkRequest(GsmResolveNetworkRequest resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (_worker is null || _worker.IsCurrentThread)
        {
            return ResolveNetworkRequestCore(resolution);
        }
        return _worker.Invoke(() => ResolveNetworkRequestCore(resolution));
    }

    bool ResolveNetworkRequestCore(GsmResolveNetworkRequest resolution)
    {
        if (_pendingOutgoingRequest is not { } pending ||
            pending.Request.RequestId != resolution.RequestId)
        {
            return false;
        }

        _pendingOutgoingRequest = null;
        ResolvePendingNetworkRequest(pending, resolution.Decision);
        return true;
    }

    void ResolvePendingNetworkRequest(
        GsmPendingOutgoingNetworkRequest pending,
        GsmNetworkRequestDecision decision)
    {
        if (pending.Request.Kind == GsmNetworkRequestKind.Call)
        {
            ResolvePendingCall(pending, decision);
        }
        else
        {
            ResolvePendingSms(pending, decision);
        }
    }

    void ResolvePendingCall(
        GsmPendingOutgoingNetworkRequest pending,
        GsmNetworkRequestDecision decision)
    {
        var accepted = decision == GsmNetworkRequestDecision.Accept;
        var information = accepted
            ? new byte[]
            {
                (byte)(pending.TransactionAndProtocolDiscriminator ^ 0x80),
                0x02, // CALL PROCEEDING
            }
            : new byte[]
            {
                (byte)(pending.TransactionAndProtocolDiscriminator ^ 0x80),
                0x2a, // RELEASE COMPLETE
                0x02, 0x80, 0x11, // cause: user busy
            };
        EnqueueAcknowledgedInformation(
            pending.Sapi,
            information,
            accepted
                ? GsmCompatibilityCellDedicatedDownlinkFrameKind.CallProceeding
                : GsmCompatibilityCellDedicatedDownlinkFrameKind.CallReleaseComplete);
        if (accepted)
        {
            _activeCallSapi = pending.Sapi;
            _activeCallTransactionAndProtocolDiscriminator =
                pending.TransactionAndProtocolDiscriminator;
            _awaitingCallProceedingAcknowledgement = true;
            Interlocked.Increment(ref _callProceedingCount);
        }
    }

    void ResolvePendingSms(
        GsmPendingOutgoingNetworkRequest pending,
        GsmNetworkRequestDecision decision)
    {
        if (decision == GsmNetworkRequestDecision.Accept)
        {
            EnqueueSmsRpAck(
                pending.Sapi,
                pending.TransactionAndProtocolDiscriminator,
                pending.MessageReference);
            return;
        }

        EnqueueAcknowledgedInformation(
            pending.Sapi,
            [
                (byte)(pending.TransactionAndProtocolDiscriminator ^ 0x80),
                0x01,
                0x04,
                0x05,
                pending.MessageReference,
                0x01,
                0x15,
            ],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsRpError);
    }

    public void QueueIncomingSms(string originator, string text)
    {
        ArgumentNullException.ThrowIfNull(originator);
        ArgumentNullException.ThrowIfNull(text);
        if (!originator.Any(char.IsDigit))
        {
            throw new ArgumentException(
                "Incoming SMS originators must contain at least one digit.",
                nameof(originator));
        }
        if (_worker is null || _worker.IsCurrentThread)
        {
            _incomingSms.Enqueue(new(originator, text));
        }
        else
        {
            _worker.Invoke(() => _incomingSms.Enqueue(new(originator, text)));
        }
        Interlocked.Increment(ref _queuedIncomingSmsCount);
    }

    /// <summary>
    /// Queues a network-originated circuit-switched call for the next native
    /// idle paging opportunity.
    /// </summary>
    public void QueueIncomingCall(string originator)
    {
        ArgumentNullException.ThrowIfNull(originator);
        var digits = NormalizeDialledNumber(originator);
        if (digits.Length is 0 or > 13)
        {
            throw new ArgumentException(
                "Incoming call originators must contain one to thirteen digits.",
                nameof(originator));
        }
        if (_worker is null || _worker.IsCurrentThread)
        {
            _incomingCalls.Enqueue(new(digits, originator.StartsWith('+')));
        }
        else
        {
            _worker.Invoke(() =>
                _incomingCalls.Enqueue(new(digits, originator.StartsWith('+'))));
        }
        Interlocked.Increment(ref _queuedIncomingCallCount);
    }

    public AsicFchResult Detect(AsicFchRequest request)
    {
        Interlocked.Increment(ref _fchAttemptCount);
        if (RawSample < MinimumRawSample ||
            !request.RfTransactions.Any(transaction =>
                AsicRfFrontend.TryDecodeBandZeroArfcn(
                    transaction.Slot,
                    transaction,
                    out var tunedArfcn) &&
                tunedArfcn == Arfcn))
        {
            return AsicFchResult.Failure;
        }

        Interlocked.Increment(ref _fchSuccessCount);
        return new(true, new byte[]
        {
            0x00, 0x00,
            unchecked((byte)AcceptedFchMetric),
            (byte)(AcceptedFchMetric >> 8),
            0x00, 0x00, 0x00,
        });
    }

    public AsicChannelDecoderResult DecodeSch(ReadOnlyMemory<byte> input)
    {
        if (input.Length != AsicChannelDecoder.SchInputLength)
        {
            return AsicChannelDecoderResult.Failure;
        }

        Interlocked.Increment(ref _schDecodeCount);
        return new(true, BuildSchInformation(Bsic, DefaultSchFrameNumber));
    }

    public AsicChannelDecoderResult DecodeControlChannel(
        ReadOnlyMemory<byte> input) =>
        DecodeControlChannel(new AsicControlChannelDecodeRequest(
            input,
            DedicatedDownlinkDecoderState));

    public AsicChannelDecoderResult DecodeControlChannel(
        AsicControlChannelDecodeRequest request)
    {
        var input = request.Input;
        if (input.Length != AsicChannelDecoder.ControlChannelInputLength)
        {
            return AsicChannelDecoderResult.Failure;
        }

        Interlocked.Increment(ref _controlChannelDecodeCount);
        Interlocked.Increment(
            ref _controlChannelStateCounts[request.FirmwareState]);
        _controlChannelDecodeObserved?.Invoke(request.FirmwareState);
        var reference = _randomAccessReferenceSource?.Invoke();
        if (_worker is null || _worker.IsCurrentThread)
        {
            return DecodeControlChannelCore(request, reference);
        }
        return _worker.Invoke(() => DecodeControlChannelCore(request, reference));
    }

    AsicChannelDecoderResult DecodeControlChannelCore(
        AsicControlChannelDecodeRequest request,
        GsmRandomAccessReference? reference)
    {
        RememberControlChannelState(request.FirmwareState);

        var dedicatedResponse = GetDedicatedChannelResponse(request.FirmwareState);
        if (dedicatedResponse is not null)
        {
            return dedicatedResponse;
        }

        var assignment = GetImmediateAssignment(reference);
        if (assignment is not null)
        {
            return assignment;
        }

        var paging = GetPagingResponse(request.FirmwareState);
        return paging ?? DecodeBroadcastControlChannel(request.FirmwareState);
    }

    void RememberControlChannelState(byte firmwareState)
    {
        if (firmwareState != CellAcquisitionDecoderState)
        {
            _lastControlChannelFirmwareState = firmwareState;
        }
    }

    AsicChannelDecoderResult? GetDedicatedChannelResponse(byte firmwareState)
        => GetActiveSacchResponse(firmwareState) ??
            GetDedicatedTransitionResponse(firmwareState) ??
            GetQueuedDedicatedResponse() ??
            GetActiveDedicatedFillResponse();

    AsicChannelDecoderResult? GetActiveSacchResponse(byte firmwareState) =>
        IsActiveSacchRequest(firmwareState)
            ? GetNextSacchResponse()
            : null;

    AsicChannelDecoderResult? GetDedicatedTransitionResponse(byte firmwareState) =>
        _dedicatedLinkActive && !IsDedicatedDecoderState(firmwareState)
            ? LeaveDedicatedChannelOrWait()
            : null;

    AsicChannelDecoderResult? GetQueuedDedicatedResponse()
    {
        if (!_dedicatedDownlinkFrames.TryDequeue(out var frame))
        {
            return null;
        }

        ObserveDeliveredFrame(frame.Kind);
        return new(true, frame.Bytes);
    }

    AsicChannelDecoderResult? GetActiveDedicatedFillResponse() =>
        _dedicatedLinkActive
            ? new(true, BuildLapdmFillFrame())
            : null;

    bool IsActiveSacchRequest(byte firmwareState) =>
        _dedicatedLinkActive &&
        firmwareState == DedicatedSacchDecoderState &&
        !_channelReleaseDelivered &&
        !_smsTransactionComplete &&
        !_incomingCallReleaseComplete;

    AsicChannelDecoderResult GetNextSacchResponse()
    {
        // SACCH has a two-byte physical header and 21 LAPDm bytes. Alternating
        // SI5 and SI6 keeps the native radio-link monitor refreshed.
        _dedicatedSacchIndex++;
        var bytes = (_dedicatedSacchIndex & 1) == 0
            ? BuildSacchSystemInformation6(_locationAreaIdentity, Bsic)
            : BuildSacchSystemInformation5();
        return new(true, bytes);
    }

    bool IsDedicatedDecoderState(byte firmwareState) =>
        firmwareState == DedicatedDownlinkDecoderState ||
        ((_mobileTerminatedTrafficAssignmentInProgress ||
          _assignedSignallingChannelActive) &&
         firmwareState == AssignedSignallingDownlinkDecoderState);

    AsicChannelDecoderResult? LeaveDedicatedChannelOrWait()
    {
        if (!_channelReleaseDelivered && !_smsTransactionComplete &&
            !_incomingCallReleaseComplete)
        {
            return new(true, BuildLapdmFillFrame());
        }

        // The first non-dedicated decoder request after release is the native
        // boundary at which idle BCCH/CCCH may resume.
        var releasedByMobile =
            (_smsTransactionComplete && !_channelReleaseDelivered) ||
            _incomingCallReleaseComplete;
        ReleaseDedicatedConnection(releasedByMobile);
        return null;
    }

    void ObserveDeliveredFrame(
        GsmCompatibilityCellDedicatedDownlinkFrameKind kind)
    {
        switch (kind)
        {
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.Ua:
                Interlocked.Increment(ref _uaCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.LocationUpdatingAccept:
                Interlocked.Increment(ref _locationUpdatingAcceptCount);
                _awaitingLocationUpdatingAcceptAcknowledgement = true;
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.ChannelRelease:
                Interlocked.Increment(ref _channelReleaseCount);
                _channelReleaseDelivered = true;
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.CipheringModeCommand:
                Interlocked.Increment(ref _cipheringModeCommandCount);
                _awaitingCipheringModeComplete = true;
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsCpAck:
                Interlocked.Increment(ref _smsCpAckCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsRpAck:
                Interlocked.Increment(ref _smsRpAckCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsRpError:
                Interlocked.Increment(ref _smsRpErrorCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.PagingSapi3Sabm:
                Interlocked.Increment(ref _mobileTerminatedSapi3EstablishmentCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedSmsCpData:
                Interlocked.Increment(ref _mobileTerminatedSmsCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedCallSetup:
                Interlocked.Increment(ref _mobileTerminatedCallSetupCount);
                break;
            case GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedTrafficAssignment:
                Interlocked.Increment(ref _mobileTerminatedTrafficAssignmentDeliveredCount);
                break;
        }
    }

    AsicChannelDecoderResult? GetImmediateAssignment(
        GsmRandomAccessReference? reference)
    {
        if (reference is null ||
            _pendingRandomAccessRequestReference != reference.Value.RequestReference)
        {
            return null;
        }

        _pendingRandomAccessRequestReference = null;
        Interlocked.Increment(ref _immediateAssignmentCount);
        return new(true, BuildImmediateAssignment(reference.Value, Bsic, Arfcn));
    }

    AsicChannelDecoderResult? GetPagingResponse(byte firmwareState)
    {
        var incomingPagingReady = _incomingPagingReadySource?.Invoke() ??
            MobileDedicatedReleaseCount != 0;
        if (!_registered || !incomingPagingReady ||
            firmwareState != IdlePagingDecoderState)
        {
            return null;
        }

        ActivateNextIncomingTransaction();
        if (_incomingPagingAnswered ||
            (_activeIncomingSms is null && _activeIncomingCall is null) ||
            _incomingPagingAttemptCount >= MaximumPagingAttempts)
        {
            return null;
        }

        _incomingPageOutstanding = true;
        _incomingPagingAttemptCount++;
        Interlocked.Increment(ref _pagingRequestCount);
        return new(true, BuildPagingRequestType1(Imsi));
    }

    void ActivateNextIncomingTransaction()
    {
        if (_activeIncomingSms is null && _activeIncomingCall is null &&
            _incomingSms.TryDequeue(out var incomingSms))
        {
            _activeIncomingSms = incomingSms;
            _activeIncomingSmsMessageReference =
                _nextIncomingSmsMessageReference++;
            _incomingPagingAttemptCount = 0;
        }
        if (_activeIncomingSms is null && _activeIncomingCall is null &&
            _incomingCalls.TryDequeue(out var incomingCall))
        {
            _activeIncomingCall = incomingCall;
            _incomingPagingAttemptCount = 0;
        }
    }

    AsicChannelDecoderResult DecodeBroadcastControlChannel(byte firmwareState)
    {
        if (firmwareState == CellAcquisitionDecoderState)
        {
            return DecodeCellAcquisitionBroadcast();
        }

        var index = Interlocked.Increment(ref _controlChannelIndex);
        var bytes = (index & 3) switch
        {
            0 => BuildSystemInformation3(_locationAreaIdentity),
            1 => BuildSystemInformation4(_locationAreaIdentity),
            2 => BuildSystemInformation2(),
            // This decoded-channel boundary cannot communicate the absence
            // of SI1 through an exact BCCH TC schedule. Emit a valid explicit
            // SI1 so ReleasedTryServing can complete its native observed-SI
            // mask instead of waiting indefinitely after dedicated release.
            _ => BuildSystemInformation1(Arfcn),
        };
        return new(true, bytes);
    }

    AsicChannelDecoderResult DecodeCellAcquisitionBroadcast()
    {
        // Every native candidate-cell run starts at SI3 rather than inheriting
        // the phase of the idle BCCH rotation.
        if (_lastControlChannelFirmwareState != CellAcquisitionDecoderState)
        {
            _cellAcquisitionIndex = 0;
        }
        var acquisitionIndex = _cellAcquisitionIndex++;
        _lastControlChannelFirmwareState = CellAcquisitionDecoderState;
        var bytes = (acquisitionIndex & 3) switch
        {
            0 => BuildSystemInformation3(_locationAreaIdentity),
            1 => BuildSystemInformation4(_locationAreaIdentity),
            2 => BuildSystemInformation2(),
            _ => BuildSystemInformation1(Arfcn),
        };
        return new(true, bytes);
    }

    internal bool ReleaseAbandonedIncomingCall()
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            return ReleaseAbandonedIncomingCallCore();
        }
        return _worker.Invoke(ReleaseAbandonedIncomingCallCore);
    }

    bool ReleaseAbandonedIncomingCallCore()
    {
        {
            if (!_dedicatedLinkActive || !_incomingCallTransactionActive)
            {
                return false;
            }

            // The native RR task can abandon an unserviceable call or leave
            // the traffic channel immediately after sending DISCONNECT,
            // without issuing another dedicated decoder command. Either is
            // an observable mobile release boundary even though
            // DecodeControlChannel cannot see the usual first non-dedicated
            // request.
            ReleaseDedicatedConnection(releasedByMobile: true);
            return true;
        }
    }

    void ReleaseDedicatedConnection(bool releasedByMobile)
    {
        _dedicatedLinkActive = false;
        _awaitingLocationUpdatingAcceptAcknowledgement = false;
        _awaitingCipheringModeComplete = false;
        _channelReleaseQueued = false;
        _channelReleaseDelivered = false;
        Volatile.Write(ref _mmConnectionActive, false);
        _incomingPagingAnswered = false;
        _incomingPageOutstanding = false;
        _incomingSmsTransactionActive = false;
        _incomingCallTransactionActive = false;
        _incomingCallReleaseComplete = false;
        _mobileTerminatedTrafficAssignmentInProgress = false;
        _assignedSignallingChannelActive = false;
        _awaitingIncomingSapi3Ua = false;
        _awaitingCallProceedingAcknowledgement = false;
        _awaitingCallAlertingAcknowledgement = false;
        if (_activeIncomingSms is not null)
        {
            _incomingPagingAttemptCount = 0;
        }
        if (_activeIncomingCall is not null)
        {
            _activeIncomingCall = null;
            _incomingPagingAttemptCount = 0;
        }
        if (releasedByMobile)
        {
            Interlocked.Increment(ref _mobileDedicatedReleaseCount);
        }
        _smsTransactionComplete = false;
        _pendingOutgoingRequest = null;
        _dedicatedDownlinkFrames.Clear();
        ResetAllLapdmLinks();
        Volatile.Write(ref _registered, true);
    }

    public void Encode(ReadOnlyMemory<byte> input) =>
        Encode(new AsicChannelEncoderRequest(
            input,
            DedicatedUplinkEncoderState));

    public void Encode(AsicChannelEncoderRequest request)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            EncodeCore(request);
        }
        else
        {
            _worker.Invoke(() => EncodeCore(request));
        }
    }

    void EncodeCore(AsicChannelEncoderRequest request)
    {
        if (ObserveRandomAccessRequest(request))
        {
            return;
        }

        if (!ObserveDedicatedUplink(request))
        {
            return;
        }

        var input = request.Input.Span;
        if (HandleUaResponse(input) || HandleSabm(input) ||
            HandleCipheringModeComplete(input) || HandleInformation(input))
        {
            return;
        }

        HandleReceiveReady(input);
    }

    bool ObserveRandomAccessRequest(AsicChannelEncoderRequest request)
    {
        if (request.FirmwareState != RandomAccessUplinkEncoderState)
        {
            return false;
        }

        RecordRandomAccessRequest(request.Input.Span);
        return true;
    }

    void RecordRandomAccessRequest(ReadOnlySpan<byte> input)
    {
        if (input.Length != 1)
        {
            return;
        }

        _pendingRandomAccessRequestReference = input[0];
        AcceptOutstandingIncomingPage();
        Interlocked.Increment(ref _randomAccessRequestCount);
    }

    void AcceptOutstandingIncomingPage()
    {
        if (_registered &&
            (_activeIncomingSms is not null || _activeIncomingCall is not null) &&
            _incomingPageOutstanding)
        {
            _incomingPagingAnswered = true;
            _incomingPageOutstanding = false;
        }
    }

    bool ObserveDedicatedUplink(AsicChannelEncoderRequest request)
    {
        if (request.Input.Length != AsicChannelDecoder.ControlChannelOutputLength)
        {
            return false;
        }

        _dedicatedUplinkFrameObserved?.Invoke(new(
            request.FirmwareState,
            request.Input.ToArray()));
        if (request.FirmwareState is not (
                DedicatedUplinkEncoderState or TrafficUplinkEncoderState))
        {
            return false;
        }
        Interlocked.Increment(ref _dedicatedUplinkCount);
        return true;
    }

    bool HandleUaResponse(ReadOnlySpan<byte> input)
    {
        if (!TryGetUaResponse(input, out var sapi))
        {
            return false;
        }

        if (_awaitingIncomingSapi3Ua && _incomingSmsTransactionActive &&
            _activeIncomingSms is { } incomingSms && sapi == 3)
        {
            _awaitingIncomingSapi3Ua = false;
            EnqueueAcknowledgedInformationSegments(
                sapi,
                GsmSmsCodec.BuildMobileTerminatedCpData(
                    incomingSms.Originator,
                    incomingSms.Text,
                    _activeIncomingSmsMessageReference,
                    _networkTimeProvider()),
                GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedSmsCpData);
        }
        return true;
    }

    bool HandleSabm(ReadOnlySpan<byte> input)
    {
        if (!TryBuildUaForSabm(input, out var ua, out var sapi,
                out var information))
        {
            return false;
        }

        var locationUpdate = IsLocationUpdatingRequest(information);
        var cmService = IsCmServiceRequest(information);
        var acceptsPaging = AcceptsIncomingPagingResponse(sapi, information);
        PrepareSabmConnection(sapi, information, acceptsPaging);
        _dedicatedDownlinkFrames.Enqueue(new(
            ua,
            GsmCompatibilityCellDedicatedDownlinkFrameKind.Ua));
        EnqueueLocationUpdateResponse(sapi, locationUpdate);
        EnqueueCipheringResponse(sapi, cmService || acceptsPaging);
        ObserveAcceptedPagingResponse(acceptsPaging);
        Interlocked.Increment(ref _sabmCount);
        if (cmService)
        {
            Interlocked.Increment(ref _cmServiceRequestCount);
        }
        return true;
    }

    bool AcceptsIncomingPagingResponse(byte sapi, ReadOnlySpan<byte> information) =>
        sapi == 0 && IsPagingResponse(information) &&
        (_activeIncomingSms is not null || _activeIncomingCall is not null) &&
        _incomingPagingAnswered;

    void PrepareSabmConnection(
        byte sapi,
        ReadOnlySpan<byte> information,
        bool acceptsPaging)
    {
        var sapi3Establishment = sapi == 3 && information.Length == 0 &&
            _mmConnectionActive;
        var assignmentReestablishment = sapi == 0 &&
            information.Length == 0 &&
            _mobileTerminatedTrafficAssignmentInProgress;
        if (!sapi3Establishment && !assignmentReestablishment)
        {
            ResetForNewDedicatedConnection(acceptsPaging);
        }
        else if (assignmentReestablishment)
        {
            ResetForAssignmentReestablishment();
        }
        ResetLapdmLink(sapi);
    }

    void ResetForNewDedicatedConnection(bool acceptsPaging)
    {
        _dedicatedLinkActive = true;
        _awaitingLocationUpdatingAcceptAcknowledgement = false;
        _awaitingCipheringModeComplete = false;
        _channelReleaseQueued = false;
        _channelReleaseDelivered = false;
        Volatile.Write(ref _mmConnectionActive, false);
        _smsTransactionComplete = false;
        _pendingOutgoingRequest = null;
        _awaitingCallProceedingAcknowledgement = false;
        _awaitingCallAlertingAcknowledgement = false;
        _incomingSmsTransactionActive = acceptsPaging &&
            _activeIncomingSms is not null;
        _incomingCallTransactionActive = acceptsPaging &&
            _activeIncomingCall is not null;
        _incomingCallReleaseComplete = false;
        _mobileTerminatedTrafficAssignmentInProgress = false;
        _assignedSignallingChannelActive = false;
        _awaitingIncomingSapi3Ua = false;
        _dedicatedDownlinkFrames.Clear();
        ResetAllLapdmLinks();
    }

    void ResetForAssignmentReestablishment()
    {
        _dedicatedLinkActive = true;
        Volatile.Write(ref _mmConnectionActive, true);
        _dedicatedDownlinkFrames.Clear();
        ResetAllLapdmLinks();
    }

    void EnqueueLocationUpdateResponse(byte sapi, bool locationUpdate)
    {
        if (!locationUpdate)
        {
            return;
        }

        _dedicatedDownlinkFrames.Enqueue(new(
            BuildLocationUpdatingAcceptFrame(sapi),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.LocationUpdatingAccept));
        _nextDownlinkSendSequences[sapi] = 1;
    }

    void EnqueueCipheringResponse(byte sapi, bool required)
    {
        if (!required)
        {
            return;
        }

        _dedicatedDownlinkFrames.Enqueue(new(
            BuildCipheringModeCommandFrame(sapi),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.CipheringModeCommand));
        _nextDownlinkSendSequences[sapi] = 1;
    }

    void ObserveAcceptedPagingResponse(bool accepted)
    {
        if (accepted)
        {
            _incomingPagingAnswered = false;
            Interlocked.Increment(ref _pagingResponseCount);
        }
    }

    bool HandleCipheringModeComplete(ReadOnlySpan<byte> input)
    {
        if (!TryGetInformationFrame(input, out var frame) || frame.MoreData ||
            !IsCipheringModeComplete(frame.Information))
        {
            return false;
        }

        Interlocked.Increment(ref _dedicatedUplinkInformationCount);
        if (_awaitingCipheringModeComplete &&
            frame.SendSequence == _nextUplinkReceiveSequences[frame.Sapi] &&
            frame.ReceiveSequence == 1)
        {
            CompleteCipheringMode(
                frame.Sapi,
                frame.SendSequence,
                frame.PollFinal);
        }
        return true;
    }

    void CompleteCipheringMode(byte sapi, byte sendSequence, bool pollFinal)
    {
        _nextUplinkReceiveSequences[sapi] = (byte)((sendSequence + 1) & 0x07);
        _awaitingCipheringModeComplete = false;
        Volatile.Write(ref _mmConnectionActive, true);
        _dedicatedDownlinkFrames.Enqueue(new(
            BuildReceiveReadyFrame(
                sapi,
                _nextUplinkReceiveSequences[sapi],
                pollFinal),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.ReceiveReady));
        if (_incomingSmsTransactionActive)
        {
            StartIncomingSmsTransfer(sapi);
        }
        else if (_incomingCallTransactionActive &&
            _activeIncomingCall is { } incomingCall)
        {
            EnqueueAcknowledgedInformation(
                sapi,
                BuildMobileTerminatedCallSetup(incomingCall),
                GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedCallSetup);
        }
        Interlocked.Increment(ref _cipheringModeCompleteCount);
    }

    void StartIncomingSmsTransfer(byte sapi)
    {
        EnqueueAcknowledgedInformation(
            sapi,
            BuildMmInformation(_networkTimeProvider()),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.MmInformation);
        ResetLapdmLink(3);
        _dedicatedDownlinkFrames.Enqueue(new(
            BuildSabmCommand(3),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.PagingSapi3Sabm));
        _awaitingIncomingSapi3Ua = true;
    }

    bool HandleInformation(ReadOnlySpan<byte> input)
    {
        if (!TryGetInformationFrame(input, out var frame))
        {
            return false;
        }

        Interlocked.Increment(ref _dedicatedUplinkInformationCount);
        if (!_mmConnectionActive || frame.Sapi is not (0 or 3))
        {
            return true;
        }

        var completeInformation = AddInformationSegment(
            frame.Sapi,
            frame.SendSequence,
            frame.MoreData,
            frame.Information);
        if (!MobileTerminatedResponseWillAcknowledge(
                frame.Sapi,
                completeInformation))
        {
            _dedicatedDownlinkFrames.Enqueue(new(
                BuildReceiveReadyFrame(
                    frame.Sapi,
                    _nextUplinkReceiveSequences[frame.Sapi],
                    frame.PollFinal),
                GsmCompatibilityCellDedicatedDownlinkFrameKind.ReceiveReady));
        }

        NotifyOutgoingRequest(HandleCompleteLayer3(
            frame.Sapi,
            completeInformation));
        return true;
    }

    byte[]? AddInformationSegment(
        byte sapi,
        byte sendSequence,
        bool moreData,
        ReadOnlySpan<byte> information)
    {
        if (sendSequence != _nextUplinkReceiveSequences[sapi])
        {
            return null;
        }

        _uplinkReassembly[sapi].AddRange(information);
        _nextUplinkReceiveSequences[sapi] = (byte)((sendSequence + 1) & 0x07);
        if (moreData)
        {
            return null;
        }

        var complete = _uplinkReassembly[sapi].ToArray();
        _uplinkReassembly[sapi].Clear();
        return complete;
    }

    bool MobileTerminatedResponseWillAcknowledge(
        byte sapi,
        byte[]? information) =>
        _incomingCallTransactionActive && sapi == 0 &&
        information is { Length: >= 2 } &&
        (information[0] & 0x0f) == 0x03 &&
        (information[1] & 0x3f) is 0x08 or 0x07 or 0x25 or 0x2a;

    GsmOutgoingNetworkRequest? HandleCompleteLayer3(
        byte sapi,
        byte[]? information)
    {
        if (information is null)
        {
            return null;
        }

        if (_incomingSmsTransactionActive && sapi == 3)
        {
            return HandleMobileTerminatedSmsLayer3(sapi, information);
        }
        if (_incomingCallTransactionActive && sapi == 0)
        {
            return HandleMobileTerminatedCallLayer3(information);
        }
        return HandleActiveLayer3(sapi, information);
    }

    void NotifyOutgoingRequest(GsmOutgoingNetworkRequest? request)
    {
        if (request is not null)
        {
            _outgoingNetworkRequest?.Invoke(request);
        }
    }

    void HandleReceiveReady(ReadOnlySpan<byte> input)
    {
        if (!TryGetReceiveReady(input, out var sapi, out var receiveSequence))
        {
            return;
        }

        Interlocked.Increment(ref _receiveReadyCount);
        ReleaseAfterLocationUpdate(sapi, receiveSequence);
        AdvanceOutgoingCall(sapi, receiveSequence);
    }

    void ReleaseAfterLocationUpdate(byte sapi, byte receiveSequence)
    {
        if (!_awaitingLocationUpdatingAcceptAcknowledgement ||
            receiveSequence != 1 || _channelReleaseQueued)
        {
            return;
        }

        _dedicatedDownlinkFrames.Enqueue(new(
            BuildChannelReleaseFrame(sapi),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.ChannelRelease));
        _awaitingLocationUpdatingAcceptAcknowledgement = false;
        _channelReleaseQueued = true;
    }

    void AdvanceOutgoingCall(byte sapi, byte receiveSequence)
    {
        if (sapi != _activeCallSapi ||
            receiveSequence != _nextDownlinkSendSequences[sapi])
        {
            return;
        }

        if (_awaitingCallProceedingAcknowledgement)
        {
            EnqueueCallAlerting(sapi);
        }
        else if (_awaitingCallAlertingAcknowledgement)
        {
            EnqueueCallConnect(sapi);
        }
    }

    void EnqueueCallAlerting(byte sapi)
    {
        EnqueueAcknowledgedInformation(
            sapi,
            [(byte)(_activeCallTransactionAndProtocolDiscriminator ^ 0x80), 0x01],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.CallAlerting);
        _awaitingCallProceedingAcknowledgement = false;
        _awaitingCallAlertingAcknowledgement = true;
        Interlocked.Increment(ref _callAlertingCount);
    }

    void EnqueueCallConnect(byte sapi)
    {
        EnqueueAcknowledgedInformation(
            sapi,
            [(byte)(_activeCallTransactionAndProtocolDiscriminator ^ 0x80), 0x07],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.CallConnect);
        _awaitingCallAlertingAcknowledgement = false;
        Interlocked.Increment(ref _callConnectCount);
    }

    public static byte[] BuildLapdmFillFrame()
    {
        var frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = 0x03;
        frame[1] = 0x03;
        frame[2] = 0x01;
        return frame;
    }

    /// <summary>
    /// Builds the mandatory SACCH System Information Type 5 block. The empty
    /// bitmap-zero neighbour list is intentional for this single-cell facade.
    /// </summary>
    public static byte[] BuildSacchSystemInformation5() =>
    [
        DefaultMsTxPowerMaxCch, 0x00,
        0x03, 0x03, 0x49,
        0x06, 0x1d,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>
    /// Builds the mandatory SACCH System Information Type 6 block from the
    /// compatibility cell identity. Radio-link timeout is encoded at its
    /// standards-defined maximum (64 SACCH periods); valid received blocks
    /// still drive the firmware's native decrement/replenishment algorithm.
    /// </summary>
    public static byte[] BuildSacchSystemInformation6(
        ReadOnlySpan<byte> locationAreaIdentity,
        byte bsic)
    {
        ValidateLocationAreaIdentity(locationAreaIdentity);
        if (bsic > 0x3f)
        {
            throw new ArgumentOutOfRangeException(nameof(bsic));
        }

        return
        [
            DefaultMsTxPowerMaxCch, 0x00,
            0x03, 0x03, 0x2d,
            0x06, 0x1e,
            0x00, 0x01,
            locationAreaIdentity[0],
            locationAreaIdentity[1],
            locationAreaIdentity[2],
            locationAreaIdentity[3],
            locationAreaIdentity[4],
            0x0f,
            (byte)(1 << (bsic >> 3)),
            0x2b, 0x2b, 0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
        ];
    }

    public static bool TryBuildUaForSabm(
        ReadOnlySpan<byte> uplink,
        out byte[] ua)
    {
        return TryBuildUaForSabm(
            uplink,
            out ua,
            out _,
            out _);
    }

    static bool TryBuildUaForSabm(
        ReadOnlySpan<byte> uplink,
        out byte[] ua,
        out byte sapi,
        out byte[] information)
    {
        ua = [];
        sapi = 0;
        information = [];
        if (uplink.Length != AsicChannelDecoder.ControlChannelOutputLength)
        {
            return false;
        }

        var address = uplink[0];
        var control = uplink[1];
        var lengthIndicator = uplink[2];
        var informationLength = lengthIndicator >> 2;
        if ((address & 0x01) == 0 ||
            (control & 0xef) != 0x2f ||
            (lengthIndicator & 0x03) != 0x01 ||
            informationLength > uplink.Length - 3)
        {
            return false;
        }

        sapi = (byte)((address >> 2) & 0x07);
        information = uplink.Slice(3, informationLength).ToArray();
        ua = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        ua.AsSpan().Fill(0x2b);
        ua[0] = (byte)((sapi << 2) | 0x01);
        ua[1] = (byte)(0x63 | (control & 0x10));
        ua[2] = lengthIndicator;
        uplink.Slice(3, informationLength).CopyTo(ua.AsSpan(3));
        return true;
    }

    byte[] BuildLocationUpdatingAcceptFrame(byte sapi)
    {
        byte[] frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = (byte)((sapi << 2) | 0x03);
        frame[1] = 0x00;
        frame[2] = 0x1d;
        frame[3] = 0x05;
        frame[4] = 0x02;
        _locationAreaIdentity.CopyTo(frame, 5);
        return frame;
    }

    static byte[] BuildChannelReleaseFrame(
        byte sapi,
        byte sendSequence = 1,
        byte receiveSequence = 0)
    {
        byte[] frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = (byte)((sapi << 2) | 0x03);
        frame[1] = (byte)(
            ((receiveSequence & 0x07) << 5) |
            ((sendSequence & 0x07) << 1));
        frame[2] = 0x0d;
        frame[3] = 0x06;
        frame[4] = 0x0d;
        frame[5] = 0x00;
        return frame;
    }

    static byte[] BuildCipheringModeCommandFrame(byte sapi)
    {
        byte[] frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = (byte)((sapi << 2) | 0x03);
        frame[1] = 0x00;
        frame[2] = 0x0d;
        frame[3] = 0x06;
        frame[4] = 0x35;
        // GSM 04.08 3.4.7.2 and 10.5.2.9 permit a valid mode-setting
        // exchange that remains unciphered. The compatibility cell has not
        // authenticated a cipher key, so it must not request A5/1 here.
        frame[5] = 0x00;
        return frame;
    }

    static byte[] BuildMmInformation(DateTimeOffset networkTime) =>
    [
        0x05,
        0x32,
        0x47,
        .. GsmSmsCodec.BuildTimestampAndTimeZone(networkTime),
    ];

    static byte[] BuildMobileTerminatedCallSetup(GsmCompatibilityCellGsmIncomingCall incomingCall)
    {
        var packedDigits = PackBcdDigits(incomingCall.Originator);
        byte[] information =
        [
            // The mobile-terminated SETUP uses the network's TI 0 allocation
            // on this firmware-visible connection. Its direction is conveyed
            // by the message flow, not by flipping this octet.
            0x03,
            0x05, // SETUP
            // Reuse the complete speech bearer capability emitted by the
            // handset's proven mobile-originated SETUP path.
            0x04, 0x04, 0x60, 0x02, 0x00, 0x81,
            // Retain the standard alerting signal information element used by
            // the proven mobile-terminated path.
            0x34, 0x01,
            0x5c, (byte)(packedDigits.Length + 1),
            incomingCall.International ? (byte)0x91 : (byte)0x81,
            .. packedDigits,
        ];
        return information;
    }

    static byte[] BuildSabmCommand(byte sapi)
    {
        byte[] frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = (byte)((sapi << 2) | 0x03);
        frame[1] = 0x3f;
        frame[2] = 0x01;
        return frame;
    }

    static byte[] BuildMobileTerminatedTrafficAssignment(
        byte bsic,
        short arfcn)
    {
        var absoluteArfcn = unchecked((ushort)arfcn) & 0x03ff;
        var tsc = bsic & 0x07;
        return
        [
            0x06, 0x2e, // RR ASSIGNMENT COMMAND
            // Channel Description 2: non-hopping TCH/F on TN2. Unlike the
            // earlier experiment, this is sent at the normal CALL CONFIRMED
            // boundary while RR still owns the assignment transition.
            0x0a,
            (byte)((tsc << 5) | (absoluteArfcn >> 8)),
            (byte)absoluteArfcn,
            0x07,       // Power Command level 7, matching cell access data.
            0x63, 0x01, // Channel Mode: full-rate speech version 1.
        ];
    }

    static byte[] BuildReceiveReadyFrame(
        byte sapi,
        byte receiveSequence,
        bool final = false)
    {
        byte[] frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = (byte)((sapi << 2) | 0x01);
        frame[1] = (byte)(
            ((receiveSequence & 0x07) << 5) |
            (final ? 0x11 : 0x01));
        frame[2] = 0x01;
        return frame;
    }

    static bool TryGetInformationFrame(
        ReadOnlySpan<byte> uplink,
        out GsmLapdmUplinkFrame frame)
    {
        frame = default;
        if (uplink.Length != AsicChannelDecoder.ControlChannelOutputLength)
        {
            return false;
        }

        var address = uplink[0];
        var control = uplink[1];
        var lengthIndicator = uplink[2];
        var informationLength = lengthIndicator >> 2;
        if ((address & 0x01) == 0 ||
            (control & 0x01) != 0 ||
            (lengthIndicator & 0x01) == 0 ||
            informationLength == 0 ||
            informationLength > uplink.Length - 3 ||
            informationLength > 20)
        {
            return false;
        }

        var moreData = (lengthIndicator & 0x02) != 0;
        if (moreData && informationLength != 20)
        {
            return false;
        }
        frame = new(
            (byte)((address >> 2) & 0x07),
            (byte)((control >> 1) & 0x07),
            (byte)(control >> 5),
            moreData,
            (control & 0x10) != 0,
            uplink.Slice(3, informationLength));
        return true;
    }

    static bool TryGetUaResponse(
        ReadOnlySpan<byte> uplink,
        out byte sapi)
    {
        sapi = 0;
        if (uplink.Length != AsicChannelDecoder.ControlChannelOutputLength ||
            (uplink[0] & 0x01) == 0 ||
            (uplink[1] & 0xef) != 0x63 ||
            uplink[2] != 0x01)
        {
            return false;
        }

        sapi = (byte)((uplink[0] >> 2) & 0x07);
        return true;
    }

    GsmOutgoingNetworkRequest? HandleMobileTerminatedSmsLayer3(
        byte sapi,
        ReadOnlySpan<byte> information)
    {
        if (IsSmsCpAck(information))
        {
            Interlocked.Increment(ref _mobileTerminatedSmsCpAckCount);
            return null;
        }

        if (!TryGetMobileTerminatedRpAckReference(
                information,
                out var messageReference) ||
            messageReference != _activeIncomingSmsMessageReference ||
            _channelReleaseQueued)
        {
            return null;
        }

        Interlocked.Increment(ref _smsCpDataCount);
        Interlocked.Increment(ref _mobileTerminatedSmsRpAckCount);
        EnqueueAcknowledgedInformation(
            sapi,
            [(byte)(information[0] ^ 0x80), 0x04],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsCpAck);
        const byte mainSignallingLinkSapi = 0;
        _dedicatedDownlinkFrames.Enqueue(new(
            BuildChannelReleaseFrame(
                mainSignallingLinkSapi,
                _nextDownlinkSendSequences[mainSignallingLinkSapi],
                _nextUplinkReceiveSequences[mainSignallingLinkSapi]),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.ChannelRelease));
        _nextDownlinkSendSequences[mainSignallingLinkSapi] =
            (byte)((_nextDownlinkSendSequences[mainSignallingLinkSapi] + 1) & 0x07);
        _channelReleaseQueued = true;
        _smsTransactionComplete = true;
        _incomingSmsTransactionActive = false;
        _activeIncomingSms = null;
        _incomingPagingAttemptCount = 0;
        Interlocked.Increment(ref _deliveredIncomingSmsCount);
        return null;
    }

    GsmOutgoingNetworkRequest? HandleMobileTerminatedCallLayer3(
        ReadOnlySpan<byte> information)
    {
        if (information is [0x06, 0x29, ..])
        {
            CompleteMobileTerminatedTrafficAssignment();
            return null;
        }

        if (information.Length < 2 || (information[0] & 0x0f) != 0x03)
        {
            return null;
        }

        switch (information[1] & 0x3f)
        {
            case 0x08: // CALL CONFIRMED
                ConfirmMobileTerminatedCall();
                break;
            case 0x07: // CONNECT
                ConnectMobileTerminatedCall(information[0]);
                break;
            case 0x25: // DISCONNECT
                DisconnectMobileTerminatedCall(information[0]);
                break;
            case 0x2a: // RELEASE COMPLETE
                CompleteMobileTerminatedCallRelease();
                break;
        }
        return null;
    }

    void CompleteMobileTerminatedTrafficAssignment()
    {
        _mobileTerminatedTrafficAssignmentInProgress = false;
        _assignedSignallingChannelActive = true;
        Interlocked.Increment(
            ref _mobileTerminatedTrafficAssignmentCompleteCount);
    }

    void ConfirmMobileTerminatedCall()
    {
        EnqueueAcknowledgedInformation(
            0,
            BuildMobileTerminatedTrafficAssignment(Bsic, Arfcn),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedTrafficAssignment);
        _mobileTerminatedTrafficAssignmentInProgress = true;
        Interlocked.Increment(ref _mobileTerminatedCallConfirmedCount);
        Interlocked.Increment(ref _mobileTerminatedTrafficAssignmentCount);
    }

    void ConnectMobileTerminatedCall(byte transaction)
    {
        EnqueueAcknowledgedInformation(
            0,
            [(byte)(transaction ^ 0x80), 0x0f],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedCallConnectAcknowledge);
        Interlocked.Increment(ref _mobileTerminatedCallConnectCount);
    }

    void DisconnectMobileTerminatedCall(byte transaction)
    {
        Interlocked.Increment(ref _mobileTerminatedCallDisconnectCount);
        EnqueueAcknowledgedInformation(
            0,
            [(byte)(transaction ^ 0x80), 0x2d],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.MobileTerminatedCallRelease);
    }

    void CompleteMobileTerminatedCallRelease()
    {
        Interlocked.Increment(ref _mobileTerminatedCallReleaseCompleteCount);
        _incomingCallReleaseComplete = true;
        if (_channelReleaseQueued)
        {
            return;
        }

        const byte sapi = 0;
        _dedicatedDownlinkFrames.Enqueue(new(
            BuildChannelReleaseFrame(
                sapi,
                _nextDownlinkSendSequences[sapi],
                _nextUplinkReceiveSequences[sapi]),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.ChannelRelease));
        _nextDownlinkSendSequences[sapi] =
            (byte)((_nextDownlinkSendSequences[sapi] + 1) & 0x07);
        _channelReleaseQueued = true;
    }

    GsmOutgoingNetworkRequest? HandleActiveLayer3(
        byte sapi,
        ReadOnlySpan<byte> information) => sapi switch
        {
            0 => HandleMobileOriginatedCallLayer3(sapi, information),
            3 => HandleMobileOriginatedSmsLayer3(sapi, information),
            _ => null,
        };

    GsmOutgoingNetworkRequest? HandleMobileOriginatedSmsLayer3(
        byte sapi,
        ReadOnlySpan<byte> information)
    {
        if (IsSmsCpData(information))
        {
            return HandleMobileOriginatedCpData(sapi, information);
        }

        if (!IsSmsCpAck(information) || _channelReleaseQueued)
        {
            return null;
        }

        Interlocked.Increment(ref _mobileSmsCpAckCount);
        _smsTransactionComplete = true;
        const byte mainSignallingLinkSapi = 0;
        _dedicatedDownlinkFrames.Enqueue(new(
            BuildChannelReleaseFrame(
                mainSignallingLinkSapi,
                _nextDownlinkSendSequences[mainSignallingLinkSapi],
                _nextUplinkReceiveSequences[mainSignallingLinkSapi]),
            GsmCompatibilityCellDedicatedDownlinkFrameKind.ChannelRelease));
        _nextDownlinkSendSequences[mainSignallingLinkSapi] =
            (byte)((_nextDownlinkSendSequences[mainSignallingLinkSapi] + 1) & 0x07);
        _channelReleaseQueued = true;
        return null;
    }

    GsmOutgoingNetworkRequest? HandleMobileOriginatedCpData(
        byte sapi,
        ReadOnlySpan<byte> information)
    {
        Interlocked.Increment(ref _smsCpDataCount);
        EnqueueAcknowledgedInformation(
            sapi,
            [(byte)(information[0] ^ 0x80), 0x04],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsCpAck);
        if (!TryGetMobileOriginatedRpReference(
                information,
                out var messageReference,
                out var isMemoryAvailableNotification))
        {
            return null;
        }

        return isMemoryAvailableNotification
            ? AcknowledgeSmsMemoryAvailable(sapi, information[0], messageReference)
            : ResolveMobileOriginatedSms(sapi, information, messageReference);
    }

    GsmOutgoingNetworkRequest? AcknowledgeSmsMemoryAvailable(
        byte sapi,
        byte transaction,
        byte messageReference)
    {
        Interlocked.Increment(ref _smsRpSmmaCount);
        EnqueueSmsRpAck(sapi, transaction, messageReference);
        return null;
    }

    GsmOutgoingNetworkRequest? ResolveMobileOriginatedSms(
        byte sapi,
        ReadOnlySpan<byte> information,
        byte messageReference)
    {
        Interlocked.Increment(ref _smsRpDataCount);
        if (_outgoingNetworkRequest is null ||
            _pendingOutgoingRequest is not null ||
            !GsmSmsCodec.TryDecodeMobileOriginatedSubmit(
                information,
                out var destination,
                out var text,
                out var international))
        {
            EnqueueSmsRpAck(sapi, information[0], messageReference);
            return null;
        }

        GsmOutgoingNetworkRequest request = new(
            Guid.NewGuid(),
            GsmNetworkRequestKind.Sms,
            destination,
            text,
            international);
        _pendingOutgoingRequest = new(
            request,
            information[0],
            messageReference,
            sapi);
        Interlocked.Increment(ref _outgoingSmsRequestCount);
        return request;
    }

    GsmOutgoingNetworkRequest? HandleMobileOriginatedCallLayer3(
        byte sapi,
        ReadOnlySpan<byte> information)
    {
        // The observed SAPI-0 message is a GSM 04.08 CC SETUP: its first
        // octet carries the transaction identifier and CC discriminator,
        // while its message-type low six bits are 0x05. Preserve the exact
        // transaction octet in the response instead of fabricating a private
        // call-control ABI.
        if (_outgoingNetworkRequest is null ||
            _pendingOutgoingRequest is not null ||
            !TryDecodeMobileOriginatedCallSetup(
                information,
                out var destination,
                out var international))
        {
            return null;
        }

        GsmOutgoingNetworkRequest request = new(
            Guid.NewGuid(),
            GsmNetworkRequestKind.Call,
            destination,
            string.Empty,
            international);
        _pendingOutgoingRequest = new(
            request,
            information[0],
            MessageReference: 0,
            sapi);
        Interlocked.Increment(ref _outgoingCallRequestCount);
        return request;
    }

    void EnqueueSmsRpAck(
        byte sapi,
        byte transactionAndProtocolDiscriminator,
        byte messageReference)
    {
        EnqueueAcknowledgedInformation(
            sapi,
            [
                (byte)(transactionAndProtocolDiscriminator ^ 0x80),
                0x01,
                0x02,
                0x03,
                messageReference,
            ],
            GsmCompatibilityCellDedicatedDownlinkFrameKind.SmsRpAck);
    }

    void EnqueueAcknowledgedInformation(
        byte sapi,
        ReadOnlySpan<byte> information,
        GsmCompatibilityCellDedicatedDownlinkFrameKind kind)
    {
        _dedicatedDownlinkFrames.Enqueue(new(
            BuildInformationFrame(new(
                sapi,
                information,
                _nextDownlinkSendSequences[sapi],
                _nextUplinkReceiveSequences[sapi])),
            kind));
        _nextDownlinkSendSequences[sapi] =
            (byte)((_nextDownlinkSendSequences[sapi] + 1) & 0x07);
    }

    void EnqueueAcknowledgedInformationSegments(
        byte sapi,
        ReadOnlySpan<byte> information,
        GsmCompatibilityCellDedicatedDownlinkFrameKind finalKind)
    {
        for (var offset = 0; offset < information.Length; offset += 20)
        {
            var count = Math.Min(20, information.Length - offset);
            var moreData = offset + count < information.Length;
            _dedicatedDownlinkFrames.Enqueue(new(
                BuildInformationFrame(new(
                    sapi,
                    information.Slice(offset, count),
                    _nextDownlinkSendSequences[sapi],
                    _nextUplinkReceiveSequences[sapi],
                    moreData)),
                moreData
                    ? GsmCompatibilityCellDedicatedDownlinkFrameKind.Segment
                    : finalKind));
            _nextDownlinkSendSequences[sapi] =
                (byte)((_nextDownlinkSendSequences[sapi] + 1) & 0x07);
        }
    }

    static byte[] BuildInformationFrame(GsmLapdmInformation information)
    {
        ArgumentOutOfRangeException.ThrowIfZero(
            information.Payload.Length,
            nameof(information));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            information.Payload.Length,
            20,
            nameof(information));

        byte[] frame = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        frame.AsSpan().Fill(0x2b);
        frame[0] = (byte)((information.Sapi << 2) | 0x03);
        frame[1] = (byte)(
            ((information.ReceiveSequence & 0x07) << 5) |
            ((information.SendSequence & 0x07) << 1));
        frame[2] = (byte)(
            information.Payload.Length << 2 |
            (information.MoreData ? 0x03 : 0x01));
        information.Payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    void ResetAllLapdmLinks()
    {
        for (byte sapi = 0; sapi < _uplinkReassembly.Length; sapi++)
        {
            ResetLapdmLink(sapi);
        }
    }

    void ResetLapdmLink(byte sapi)
    {
        _nextUplinkReceiveSequences[sapi] = 0;
        _nextDownlinkSendSequences[sapi] = 0;
        _uplinkReassembly[sapi].Clear();
    }

    static bool TryGetReceiveReady(
        ReadOnlySpan<byte> uplink,
        out byte sapi,
        out byte receiveSequence)
    {
        sapi = 0;
        receiveSequence = 0;
        if (uplink.Length != AsicChannelDecoder.ControlChannelOutputLength ||
            (uplink[0] & 0x01) == 0 ||
            (uplink[1] & 0x0f) != 0x01 ||
            uplink[2] != 0x01)
        {
            return false;
        }

        sapi = (byte)((uplink[0] >> 2) & 0x07);
        receiveSequence = (byte)(uplink[1] >> 5);
        return true;
    }

    static bool IsLocationUpdatingRequest(ReadOnlySpan<byte> information) =>
        information.Length >= 2 &&
        (information[0] & 0x0f) == 0x05 &&
        information[1] == 0x08;

    static bool IsCmServiceRequest(ReadOnlySpan<byte> information) =>
        information.Length >= 3 &&
        (information[0] & 0x0f) == 0x05 &&
        information[1] == 0x24 &&
        (information[2] & 0x0f) is 0x01 or 0x02 or 0x04;

    static bool IsPagingResponse(ReadOnlySpan<byte> information) =>
        information.Length >= 2 &&
        (information[0] & 0x0f) == 0x06 &&
        information[1] == 0x27;

    static bool IsCipheringModeComplete(ReadOnlySpan<byte> information) =>
        information.Length >= 2 &&
        (information[0] & 0x0f) == 0x06 &&
        information[1] == 0x32;

    static bool IsSmsCpData(ReadOnlySpan<byte> information) =>
        information.Length >= 2 &&
        (information[0] & 0x0f) == 0x09 &&
        information[1] == 0x01;

    static bool IsSmsCpAck(ReadOnlySpan<byte> information) =>
        information.Length >= 2 &&
        (information[0] & 0x0f) == 0x09 &&
        information[1] == 0x04;

    internal static bool TryDecodeMobileOriginatedCallSetup(
        ReadOnlySpan<byte> information,
        out string destination,
        out bool international)
    {
        destination = string.Empty;
        international = false;
        if (information.Length < 7 ||
            (information[0] & 0x0f) != 0x03 ||
            (information[1] & 0x3f) != 0x05)
        {
            return false;
        }

        var number = FindCalledPartyNumber(information[2..]);
        return TryDecodeCalledPartyNumber(
            number,
            out destination,
            out international);
    }

    static ReadOnlySpan<byte> FindCalledPartyNumber(
        ReadOnlySpan<byte> elements)
    {
        if (elements.IsEmpty)
        {
            return [];
        }

        var identifier = elements[0];
        return identifier is >= 0x80 and <= 0xef
            ? FindCalledPartyNumber(elements[1..])
            : FindLengthEncodedCalledPartyNumber(identifier, elements);
    }

    static ReadOnlySpan<byte> FindLengthEncodedCalledPartyNumber(
        byte identifier,
        ReadOnlySpan<byte> elements)
    {
        if (elements.Length < 2)
        {
            return [];
        }

        var length = elements[1];
        if (length > elements.Length - 2)
        {
            return [];
        }

        return identifier == 0x5e && length >= 2
            ? elements.Slice(2, length)
            : FindCalledPartyNumber(elements[(2 + length)..]);
    }

    static bool TryDecodeCalledPartyNumber(
        ReadOnlySpan<byte> number,
        out string destination,
        out bool international)
    {
        destination = string.Empty;
        international = false;
        if (number.Length < 2)
        {
            return false;
        }

        international = (number[0] & 0x70) == 0x10;
        var encodedDigits = number[1..];
        var digitCount = encodedDigits.Length * 2;
        if ((encodedDigits[^1] >> 4) == 0x0f)
        {
            digitCount--;
        }
        return digitCount != 0 && GsmSmsCodec.TryDecodeSemiOctets(
            encodedDigits,
            digitCount,
            out destination);
    }

    static string NormalizeDialledNumber(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    static byte[] PackBcdDigits(string digits)
    {
        byte[] packed = new byte[(digits.Length + 1) / 2];
        for (var index = 0; index < digits.Length; index += 2)
        {
            var low = digits[index] - '0';
            var high = index + 1 < digits.Length
                ? digits[index + 1] - '0'
                : 0x0f;
            packed[index / 2] = (byte)(low | high << 4);
        }
        return packed;
    }

    static bool TryGetMobileOriginatedRpReference(
        ReadOnlySpan<byte> cpData,
        out byte messageReference,
        out bool isMemoryAvailableNotification)
    {
        messageReference = 0;
        isMemoryAvailableNotification = false;
        if (cpData.Length < 4)
        {
            return false;
        }

        var userDataOffset = cpData.Length >= 5 && cpData[2] == 0x01 ? 4 : 3;
        var userDataLength = cpData[userDataOffset - 1];
        if (userDataLength < 2 ||
            cpData.Length < userDataOffset + userDataLength)
        {
            return false;
        }

        var rpdu = cpData.Slice(userDataOffset, userDataLength);
        var messageType = rpdu[0] & 0x07;
        if (messageType is not (0x00 or 0x06))
        {
            return false;
        }

        messageReference = rpdu[1];
        isMemoryAvailableNotification = messageType == 0x06;
        return true;
    }

    static bool TryGetMobileTerminatedRpAckReference(
        ReadOnlySpan<byte> cpData,
        out byte messageReference)
    {
        messageReference = 0;
        if (!TryGetMobileTerminatedRpdu(cpData, out var rpdu) ||
            !IsValidMobileTerminatedRpAck(rpdu))
        {
            return false;
        }

        messageReference = rpdu[1];
        return true;
    }

    static bool TryGetMobileTerminatedRpdu(
        ReadOnlySpan<byte> cpData,
        out ReadOnlySpan<byte> rpdu)
    {
        rpdu = [];
        if (!IsSmsCpData(cpData) || cpData.Length < 5)
        {
            return false;
        }

        var userDataOffset = cpData[2] == 0x01 ? 4 : 3;
        var userDataLength = cpData[userDataOffset - 1];
        if (userDataLength < 2 ||
            cpData.Length < userDataOffset + userDataLength)
        {
            return false;
        }
        rpdu = cpData.Slice(userDataOffset, userDataLength);
        return true;
    }

    static bool IsValidMobileTerminatedRpAck(ReadOnlySpan<byte> rpdu) =>
        (rpdu[0] & 0x07) == 0x02 &&
        // GSM 04.11 7.3.3 permits RP-ACK to carry optional RP-User Data as
        // one TLV with IEI 0x41. Accept the mandatory two-octet form or that
        // exact, length-consistent extension; reject arbitrary trailing data.
        (rpdu.Length == 2 ||
         (rpdu.Length >= 4 &&
          rpdu[2] == 0x41 &&
          rpdu[3] == rpdu.Length - 4));

    public static byte[] BuildSchInformation(byte bsic, int frameNumber)
    {
        if (bsic > 0x3f)
        {
            throw new ArgumentOutOfRangeException(nameof(bsic));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(frameNumber);

        // GSM 04.08 9.1.30: BSIC(6), T1(11), T2(5), and T3'(3).
        var t1 = frameNumber / 1326 % 2048;
        var t2 = frameNumber % 26;
        var t3Prime = (frameNumber % 51 - 1) / 10;
        return
        [
            (byte)((bsic << 2) | (t1 >> 9)),
            (byte)(t1 >> 1),
            (byte)(((t1 & 1) << 7) | (t2 << 2) | (t3Prime >> 1)),
            (byte)((t3Prime & 1) << 7),
        ];
    }

    public static byte[] BuildSystemInformation2() =>
    [
        0x59, 0x06, 0x1a,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0xff,
        0x40, 0x00, 0x00,
    ];

    /// <summary>
    /// Builds System Information Type 1 with a one-channel cell allocation.
    /// Band-zero negative firmware ARFCNs are their 10-bit GSM values, so the
    /// range-1024 encoding represents the serving channel without inventing a
    /// hopping allocation.
    /// </summary>
    public static byte[] BuildSystemInformation1(short arfcn)
    {
        if (arfcn is < AsicRfFrontend.BandZeroFirstArfcn or
            > AsicRfFrontend.BandZeroLastArfcn)
        {
            throw new ArgumentOutOfRangeException(nameof(arfcn));
        }

        var absoluteArfcn = unchecked((ushort)arfcn) & 0x03ff;
        return
        [
            0x55, 0x06, 0x19,
            (byte)(0x80 | (absoluteArfcn >> 8)),
            (byte)absoluteArfcn,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00,
            0x40, 0x00, 0x00,
            0x2b,
        ];
    }

    public static byte[] BuildPagingRequestType1(
        string imsi,
        byte channelNeeded = 0)
    {
        var mobileIdentity = EncodeImsiMobileIdentityContents(imsi);
        var l2PseudoLength = 3 + 1 + mobileIdentity.Length;
        byte[] layer2 = new byte[AsicChannelDecoder.ControlChannelOutputLength];
        layer2.AsSpan().Fill(0x2b);
        layer2[0] = (byte)(l2PseudoLength << 2 | 0x01);
        layer2[1] = 0x06;
        layer2[2] = 0x21;
        layer2[3] = (byte)((channelNeeded & 0x03) << 4);
        layer2[4] = (byte)mobileIdentity.Length;
        mobileIdentity.CopyTo(layer2, 5);
        return layer2;
    }

    public static byte[] BuildSystemInformation3(
        ReadOnlySpan<byte> locationAreaIdentity)
    {
        ValidateLocationAreaIdentity(locationAreaIdentity);
        return
        [
            0x49, 0x06, 0x1b,
            0x00, 0x01,
            locationAreaIdentity[0],
            locationAreaIdentity[1],
            locationAreaIdentity[2],
            locationAreaIdentity[3],
            locationAreaIdentity[4],
            0x00, 0x00, 0x00,
            0x00,
            // GSM 04.08 10.5.2.4: GSM-900 power-control level 7
            // (29 dBm) avoids penalizing a 29 dBm mobile in the C1 test.
            DefaultMsTxPowerMaxCch, 0x00,
            0x00, 0x00, 0x00,
            0x2b, 0x2b, 0x2b, 0x2b,
        ];
    }

    public static byte[] BuildSystemInformation4(
        ReadOnlySpan<byte> locationAreaIdentity)
    {
        ValidateLocationAreaIdentity(locationAreaIdentity);
        return
        [
            0x31, 0x06, 0x1c,
            locationAreaIdentity[0],
            locationAreaIdentity[1],
            locationAreaIdentity[2],
            locationAreaIdentity[3],
            locationAreaIdentity[4],
            DefaultMsTxPowerMaxCch, 0x00,
            0x00, 0x00, 0x00,
            0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
            0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
        ];
    }

    public static byte[] BuildImmediateAssignment(
        byte requestReference,
        int requestFrameNumber,
        byte bsic,
        short arfcn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestFrameNumber);
        ValidateCellChannel(bsic, arfcn);

        // GSM 04.08 9.1.18 / 10.5.2.30: echo the CHANNEL REQUEST
        // reference and assign non-hopping SDCCH/8 subchannel zero on TN1.
        var frameNumber = requestFrameNumber % 42432;
        var t1Prime = frameNumber / 1326 % 32;
        var t3 = frameNumber % 51;
        var t2 = frameNumber % 26;
        return BuildImmediateAssignment(
            new(
                requestReference,
                (byte)t1Prime,
                (byte)t3,
                (byte)t2),
            bsic,
            arfcn);
    }

    public static byte[] BuildImmediateAssignment(
        GsmRandomAccessReference reference,
        byte bsic,
        short arfcn)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            reference.T1Prime,
            (byte)31,
            nameof(reference));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            reference.T3,
            (byte)50,
            nameof(reference));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            reference.T2,
            (byte)25,
            nameof(reference));
        ValidateCellChannel(bsic, arfcn);

        var tsc = bsic & 0x07;
        var absoluteArfcn = unchecked((ushort)arfcn) & 0x03ff;

        return
        [
            0x2d, 0x06, 0x3f, 0x00,
            0x41,
            (byte)((tsc << 5) | (absoluteArfcn >> 8)),
            (byte)absoluteArfcn,
            reference.RequestReference,
            (byte)((reference.T1Prime << 3) | (reference.T3 >> 3)),
            (byte)(((reference.T3 & 0x07) << 5) | reference.T2),
            0x00,
            0x00,
            0x2b, 0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
            0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
        ];
    }

    static void ValidateCellChannel(byte bsic, short arfcn)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bsic, (byte)0x3f);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            arfcn,
            AsicRfFrontend.BandZeroFirstArfcn);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            arfcn,
            AsicRfFrontend.BandZeroLastArfcn);
    }

    public static byte[] EncodeLocationAreaIdentity(string imsi, ushort lac)
    {
        imsi = ValidateImsi(imsi);
        var mcc1 = imsi[0] - '0';
        var mcc2 = imsi[1] - '0';
        var mcc3 = imsi[2] - '0';
        var mnc1 = imsi[3] - '0';
        var mnc2 = imsi[4] - '0';
        return
        [
            (byte)((mcc2 << 4) | mcc1),
            (byte)((0x0f << 4) | mcc3),
            (byte)((mnc2 << 4) | mnc1),
            (byte)(lac >> 8),
            (byte)lac,
        ];
    }

    static byte[] EncodeImsiMobileIdentityContents(string imsi)
    {
        imsi = ValidateImsi(imsi);
        if (imsi.Length > 15)
        {
            throw new ArgumentException(
                "Paging IMSI must contain at most fifteen decimal digits.",
                nameof(imsi));
        }

        byte[] contents = new byte[1 + imsi.Length / 2];
        var odd = (imsi.Length & 1) != 0;
        contents[0] = (byte)(
            (imsi[0] - '0') << 4 |
            (odd ? 0x09 : 0x01));
        var digit = 1;
        for (var index = 1; index < contents.Length; index++)
        {
            var low = digit < imsi.Length ? imsi[digit++] - '0' : 0x0f;
            var high = digit < imsi.Length ? imsi[digit++] - '0' : 0x0f;
            contents[index] = (byte)(low | high << 4);
        }
        return contents;
    }

    static string ValidateImsi(string imsi)
    {
        if (imsi.Length < 5 || imsi.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                "IMSI must contain at least five decimal digits.",
                nameof(imsi));
        }
        return imsi;
    }

    static void ValidateLocationAreaIdentity(ReadOnlySpan<byte> value)
    {
        if (value.Length != 5)
        {
            throw new ArgumentException(
                "Location-area identity must contain five bytes.",
                nameof(value));
        }
    }
}
