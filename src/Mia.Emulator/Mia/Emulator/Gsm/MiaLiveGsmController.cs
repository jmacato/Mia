// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Gsm;

/// <summary>
/// Core-owned live GSM facade. It supplies the RF/FCH/channel boundaries,
/// queues mobile-terminated transactions only after the native RSSI history
/// has settled, and keeps the protocol carrier active for the complete
/// dedicated transaction. Frontends only submit requests and read status.
/// </summary>
internal sealed class MiaLiveGsmController : IAsicRfSignalSource
{
    const int NativeRrStateAddress = 0x033958;
    const int NativeMmmStateAddress = 0x033299;
    const int NativeMphStateAddress = 0x02903a;
    const int NativePhCallbackStateAddress = 0x028408;
    const int NativeChannelDecoderStateAddress = 0x02a6e9;
    const int NativeRssiLevelAddress = 0x027e98;
    const int NativeRssiWidgetLevelAddress = 0x050817;
    const byte NativeIdleRrState = 3;
    const byte NativeIdleMphState = 3;
    const byte RssiActionId = 0x08;
    const ushort RssiActionOperand = 0x01da;
    const ushort RssiQuarterBit = 4475;
    const int MaximumDeferredIncomingTransactions = 8;

    readonly MiaLiveGsmOptions _options;
    readonly GsmCompatibilityCell _cell;
    readonly MiaWorker _worker;
    readonly Queue<MiaLiveGsmControllerPendingIncomingTransaction> _deferredIncomingTransactions = [];
    int _incomingTransactionActive;
    int _incomingTransactionKind;
    long _incomingSmsDeliveryBaseline;
    long _incomingCallSetupBaseline;
    long _incomingCallConfirmedBaseline;
    long _incomingCallDisconnectBaseline;
    long _incomingCallReleaseBaseline;
    int _incomingDeliveryReported;
    int _autoAnswerIncomingCall;
    long _autoAnswerPressCycle = -1;
    long _autoAnswerReleaseCycle = -1;
    ushort _lastRssiRawSample;
    long _rssiSampleCount;
    long _rrStateSeenMask;
    long _mphStateSeenMask;
    int _carrierRawSample;
    int _lastNativeRssiWriterPc = -1;
    int _lastRssiWidgetWriterPc = -1;

    internal MiaLiveGsmController(
        Cpu cpu,
        MiaLiveGsmOptions options,
        MiaWorker worker)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(worker);
        _options = options;
        _worker = worker;
        _carrierRawSample = options.IdleRssiRawSample;
        ObserveRssiWrite(
            cpu,
            NativeRssiLevelAddress,
            pc => Volatile.Write(ref _lastNativeRssiWriterPc, pc));
        ObserveRssiWrite(
            cpu,
            NativeRssiWidgetLevelAddress,
            pc => Volatile.Write(ref _lastRssiWidgetWriterPc, pc));
        GsmCompatibilityCell? cell = null;
        cell = new GsmCompatibilityCell(
            options.Arfcn,
            options.ProtocolRawSample,
            randomAccessReferenceSource: () =>
                MiaGsmRandomAccessAdapter.ReadPendingReference(cpu),
            incomingPagingReadySource: () => true,
            outgoingNetworkRequest: request =>
            {
                MessageEmitted?.Invoke(
                    $"GSM outgoing: {request.Kind} to={request.NormalizedDestination}");
                if (options.AutoAcceptOutgoingRequests)
                {
                    _ = cell!.ResolveNetworkRequest(new(
                        request.RequestId,
                        GsmNetworkRequestDecision.Accept));
                }
            },
            worker: worker);
        _cell = cell;
    }

    public event Action<string>? MessageEmitted;

    internal GsmCompatibilityCell Cell => _cell;

    public bool IncomingTransactionActive =>
        Volatile.Read(ref _incomingTransactionActive) != 0;

    public int DeferredIncomingTransactionCount
    {
        get
        {
            if (_worker.IsCurrentThread)
            {
                return _deferredIncomingTransactions.Count;
            }
            return _worker.Invoke(() => _deferredIncomingTransactions.Count);
        }
    }

    public ushort LastRssiRawSample => Volatile.Read(ref _lastRssiRawSample);

    public long RssiSampleCount => Interlocked.Read(ref _rssiSampleCount);

    public ushort CarrierRawSample
    {
        get => unchecked((ushort)Volatile.Read(ref _carrierRawSample));
        set => Volatile.Write(ref _carrierRawSample, value);
    }

    public bool TryQueueIncomingSms(string originator, string text, out string result)
    {
        ArgumentNullException.ThrowIfNull(originator);
        ArgumentNullException.ThrowIfNull(text);
        if (!originator.Any(char.IsDigit))
        {
            result = "Incoming SMS originators must contain at least one digit.";
            return false;
        }

        var fitted = GsmSmsCodec.FitMobileTerminatedTextForFirmware(
            originator,
            text);
        var transaction = new MiaLiveGsmControllerPendingIncomingTransaction(
            Kind: 1,
            Originator: originator,
            Text: fitted.Text,
            AutoAnswer: false);
        (bool queued, int deferredPosition) = _worker.IsCurrentThread
            ? TryEnqueueIncomingTransactionCore(transaction)
            : _worker.Invoke(() => TryEnqueueIncomingTransactionCore(transaction));
        if (!queued)
        {
            result =
                $"The incoming GSM queue is full ({MaximumDeferredIncomingTransactions} waiting transactions).";
            return false;
        }

        var destination = deferredPosition == 0
            ? "for the next native idle paging opportunity"
            : $"behind the active GSM transaction at position {deferredPosition}; it will start after native release returns to idle";
        result = fitted.Truncated
            ? $"Incoming SMS queued from {originator} {destination}; text was truncated to {fitted.Text.Length} characters to fit the proven T68 transfer envelope."
            : $"Incoming SMS queued from {originator} {destination}.";
        MessageEmitted?.Invoke(result);
        return true;
    }

    public bool TryQueueIncomingCall(
        string originator,
        bool autoAnswer,
        out string result)
    {
        ArgumentNullException.ThrowIfNull(originator);
        var digitCount = originator.Count(char.IsDigit);
        if (digitCount is 0 or > 13)
        {
            result = "Incoming call originators must contain one to thirteen digits.";
            return false;
        }

        var transaction = new MiaLiveGsmControllerPendingIncomingTransaction(
            Kind: 2,
            Originator: originator,
            Text: null,
            AutoAnswer: autoAnswer);
        (bool queued, int deferredPosition) = _worker.IsCurrentThread
            ? TryEnqueueIncomingTransactionCore(transaction)
            : _worker.Invoke(() => TryEnqueueIncomingTransactionCore(transaction));
        if (!queued)
        {
            result =
                $"The incoming GSM queue is full ({MaximumDeferredIncomingTransactions} waiting transactions).";
            return false;
        }

        var destination = deferredPosition == 0
            ? "for the next native idle paging opportunity"
            : $"behind the active GSM transaction at position {deferredPosition}; it will start after native release returns to idle";
        result = autoAnswer
            ? $"Incoming call queued from {originator} {destination}; auto-answer enabled."
            : $"Incoming call queued from {originator} {destination}.";
        MessageEmitted?.Invoke(result);
        return true;
    }

    void ActivateIncomingTransaction(MiaLiveGsmControllerPendingIncomingTransaction transaction)
    {
        Volatile.Write(ref _incomingDeliveryReported, 0);
        Volatile.Write(ref _incomingTransactionKind, transaction.Kind);
        Volatile.Write(ref _incomingTransactionActive, 1);
        Volatile.Write(ref _autoAnswerPressCycle, -1);
        Volatile.Write(ref _autoAnswerReleaseCycle, -1);

        if (transaction.Kind == 1)
        {
            _incomingSmsDeliveryBaseline = _cell.DeliveredIncomingSmsCount;
            Volatile.Write(ref _autoAnswerIncomingCall, 0);
            _cell.QueueIncomingSms(transaction.Originator, transaction.Text!);
            return;
        }

        _incomingCallSetupBaseline = _cell.MobileTerminatedCallSetupCount;
        _incomingCallConfirmedBaseline =
            _cell.MobileTerminatedCallConfirmedCount;
        _incomingCallDisconnectBaseline =
            _cell.MobileTerminatedCallDisconnectCount;
        _incomingCallReleaseBaseline = _cell.MobileDedicatedReleaseCount;
        Volatile.Write(
            ref _autoAnswerIncomingCall,
            transaction.AutoAnswer ? 1 : 0);
        _cell.QueueIncomingCall(transaction.Originator);
    }

    (bool Queued, int DeferredPosition) TryEnqueueIncomingTransactionCore(
        MiaLiveGsmControllerPendingIncomingTransaction transaction)
    {
        if (!IncomingTransactionActive)
        {
            ActivateIncomingTransaction(transaction);
            return (true, 0);
        }
        return TryQueueDeferredIncomingTransaction(transaction);
    }

    (bool Queued, int DeferredPosition) TryQueueDeferredIncomingTransaction(
        MiaLiveGsmControllerPendingIncomingTransaction transaction)
    {
        if (_deferredIncomingTransactions.Count >=
            MaximumDeferredIncomingTransactions)
        {
            return (false, 0);
        }
        _deferredIncomingTransactions.Enqueue(transaction);
        return (true, _deferredIncomingTransactions.Count);
    }

    public ushort ReadRawSample(
        byte adcSelector,
        AsicRfTransaction transaction,
        AsicTimeGeneratorActionExecution action)
    {
        if (!AsicRfFrontend.TryDecodeBandZeroArfcn(
                action.ProgramSelector,
                transaction,
                out var tunedArfcn) ||
            tunedArfcn != _options.Arfcn)
        {
            return 0;
        }

        if (adcSelector == _options.RssiAdcSelector &&
            action.ActionId == RssiActionId &&
            action.Operand == RssiActionOperand &&
            action.QuarterBit == RssiQuarterBit)
        {
            Interlocked.Increment(ref _rssiSampleCount);
        }

        // The firmware uses selector 1 for idle serving-cell measurements and
        // selector 2 for the averaged measurement report while a dedicated
        // channel is active. Both therefore observe the same adjustable
        // carrier amplitude. The remaining selectors retain their protocol
        // placeholder value; promoting all contexts stalls native selection.
        if (adcSelector == _options.RssiAdcSelector ||
            adcSelector == _options.DedicatedRssiAdcSelector)
        {
            var sample = CarrierRawSample;
            Volatile.Write(ref _lastRssiRawSample, sample);
            return sample;
        }
        return _options.ProtocolRawSample;
    }

    public MiaLiveGsmStatus GetStatus(MiaMachine machine) => new(
        !machine.IsStopped,
        _cell.Registered,
        machine.ExecutedInstructions,
        machine.Cycles,
        _cell.QueuedIncomingSmsCount,
        _cell.QueuedIncomingCallCount,
        _cell.PagingRequestCount,
        _cell.PagingResponseCount,
        _cell.DeliveredIncomingSmsCount,
        _cell.MobileTerminatedCallSetupCount,
        _cell.MobileTerminatedCallConfirmedCount,
        _cell.MobileTerminatedCallConnectCount,
        _cell.MobileTerminatedCallDisconnectCount,
        _cell.MobileTerminatedCallReleaseCompleteCount,
        _cell.MobileTerminatedTrafficAssignmentCount,
        _cell.MobileTerminatedTrafficAssignmentDeliveredCount,
        _cell.MobileTerminatedTrafficAssignmentCompleteCount,
        _cell.MobileDedicatedReleaseCount,
        _cell.DedicatedUplinkCount,
        _cell.DedicatedUplinkInformationCount,
        _cell.SabmCount,
        _cell.UaCount,
        _cell.CipheringModeCommandCount,
        _cell.CipheringModeCompleteCount,
        _cell.MobileTerminatedSapi3EstablishmentCount,
        _cell.MobileTerminatedSmsCount,
        machine.Cpu.Data[NativeRrStateAddress],
        machine.Cpu.Data[NativeMmmStateAddress],
        machine.Cpu.Data[NativeMphStateAddress],
        machine.Cpu.Data[NativePhCallbackStateAddress],
        machine.Cpu.Data[NativeChannelDecoderStateAddress],
        Interlocked.Read(ref _rrStateSeenMask),
        Interlocked.Read(ref _mphStateSeenMask),
        _cell.GetControlChannelStateCount(
            GsmCompatibilityCell.IdlePagingDecoderState),
        _cell.GetControlChannelStateCount(
            GsmCompatibilityCell.CellAcquisitionDecoderState),
        _cell.GetControlChannelStateCount(
            GsmCompatibilityCell.DedicatedDownlinkDecoderState),
        machine.FrameVersion,
        machine.Cpu.Data[NativeRssiLevelAddress],
        machine.Cpu.Data[NativeRssiWidgetLevelAddress],
        Volatile.Read(ref _lastNativeRssiWriterPc),
        Volatile.Read(ref _lastRssiWidgetWriterPc),
        LastRssiRawSample,
        CarrierRawSample,
        RssiSampleCount,
        machine.Adc.CompletionCount,
        machine.Adc.ResultReadCount,
        machine.Adc.GetPendingResultCount(_options.RssiAdcSelector),
        IncomingTransactionActive,
        DeferredIncomingTransactionCount);

    internal void Advance(MiaMachine machine)
    {
        var nativeMphState = machine.Cpu.Data[NativeMphStateAddress];
        Interlocked.Or(
            ref _rrStateSeenMask,
            1L << machine.Cpu.Data[NativeRrStateAddress]);
        Interlocked.Or(
            ref _mphStateSeenMask,
            1L << nativeMphState);
        machine.StatusIndicators.ObserveNetworkBoundary(
            _cell.Registered,
            nativeMphState);
        UpdateIncomingDeliveryStatus();
        ScheduleAutoAnswer(machine);

        if (!IncomingTransactionActive)
        {
            return;
        }

        int kind = Volatile.Read(ref _incomingTransactionKind);
        byte nativeRrState = machine.Cpu.Data[NativeRrStateAddress];
        ReleaseAbandonedCallIfNeeded(kind, nativeRrState, nativeMphState);
        if (!HasCompletedIncomingTransaction(
                kind,
                nativeRrState,
                nativeMphState))
        {
            return;
        }

        if (_worker.IsCurrentThread)
        {
            CompleteIncomingTransactionCore();
        }
        else
        {
            _worker.Invoke(CompleteIncomingTransactionCore);
        }
    }

    void ReleaseAbandonedCallIfNeeded(
        int kind,
        byte nativeRrState,
        byte nativeMphState)
    {
        if (kind == 2 &&
            HasNativeCallExitBoundary(
                _cell.MobileTerminatedCallSetupCount,
                Interlocked.Read(ref _incomingCallSetupBaseline),
                _cell.MobileTerminatedCallDisconnectCount,
                Interlocked.Read(ref _incomingCallDisconnectBaseline),
                nativeRrState,
                nativeMphState))
        {
            _cell.ReleaseAbandonedIncomingCall();
        }
    }

    bool HasCompletedIncomingTransaction(
        int kind,
        byte nativeRrState,
        byte nativeMphState)
    {
        bool nativeIdle =
            nativeRrState == NativeIdleRrState &&
            nativeMphState == NativeIdleMphState;
        return kind switch
        {
            1 => _cell.DeliveredIncomingSmsCount >
                Interlocked.Read(ref _incomingSmsDeliveryBaseline) && nativeIdle,
            // A connected call remains on its dedicated channel. Only a real
            // mobile/network release completes it and restores idle RSSI.
            2 => _cell.MobileDedicatedReleaseCount >
                Interlocked.Read(ref _incomingCallReleaseBaseline) && nativeIdle,
            _ => false,
        };
    }

    void CompleteIncomingTransactionCore()
    {
        Volatile.Write(ref _incomingTransactionActive, 0);
        Volatile.Write(ref _incomingTransactionKind, 0);
        Volatile.Write(ref _autoAnswerIncomingCall, 0);
        MiaLiveGsmControllerPendingIncomingTransaction? nextTransaction = null;
        if (_deferredIncomingTransactions.TryDequeue(out var next))
        {
            nextTransaction = next;
            ActivateIncomingTransaction(next);
        }
        MessageEmitted?.Invoke("Incoming GSM transaction completed after native release.");
        if (nextTransaction is { } activated)
        {
            var kindName = activated.Kind == 1 ? "SMS" : "call";
            MessageEmitted?.Invoke(
                $"Starting queued incoming {kindName} from {activated.Originator} after native idle release.");
        }
    }

    internal static bool HasNativeCallExitBoundary(
        long setupCount,
        long setupBaseline,
        long disconnectCount,
        long disconnectBaseline,
        byte nativeRrState,
        byte nativeMphState) =>
        // Before CONNECT, the native tasks jointly report the abandoned
        // dedicated channel. After a mobile DISCONNECT, RR can leave TCH/F
        // before MPH has completed its own transition.
        (setupCount > setupBaseline && nativeRrState == 1 && nativeMphState == 1) ||
        (disconnectCount > disconnectBaseline && nativeRrState == 1);

    bool IncomingDeliveryPending
    {
        get
        {
            if (!IncomingTransactionActive)
            {
                return false;
            }

            return Volatile.Read(ref _incomingTransactionKind) switch
            {
                1 => _cell.DeliveredIncomingSmsCount <=
                    Interlocked.Read(ref _incomingSmsDeliveryBaseline),
                2 => _cell.MobileTerminatedCallConfirmedCount <=
                    Interlocked.Read(ref _incomingCallConfirmedBaseline),
                _ => false,
            };
        }
    }

    void UpdateIncomingDeliveryStatus()
    {
        if (!IncomingTransactionActive || IncomingDeliveryPending)
        {
            return;
        }

        if (Interlocked.Exchange(ref _incomingDeliveryReported, 1) == 0)
        {
            var message = Volatile.Read(ref _incomingTransactionKind) == 1
                ? "Incoming SMS accepted by the handset."
                : "Incoming call reached the handset.";
            MessageEmitted?.Invoke(message);
        }
    }

    void ScheduleAutoAnswer(MiaMachine machine)
    {
        if (Volatile.Read(ref _autoAnswerIncomingCall) == 0 ||
            _cell.MobileTerminatedCallSetupCount <=
                Interlocked.Read(ref _incomingCallSetupBaseline))
        {
            return;
        }

        var pressCycle = Volatile.Read(ref _autoAnswerPressCycle);
        if (pressCycle < 0)
        {
            ScheduleAutoAnswerPress(machine.Cycles);
            return;
        }
        if (TryPressAutoAnswer(machine, pressCycle))
        {
            return;
        }
        TryReleaseAutoAnswer(machine);
    }

    void ScheduleAutoAnswerPress(long currentCycle)
    {
        long pressCycle = currentCycle + 9_000_000;
        Volatile.Write(ref _autoAnswerPressCycle, pressCycle);
        Volatile.Write(ref _autoAnswerReleaseCycle, pressCycle + 4_000_000);
    }

    bool TryPressAutoAnswer(MiaMachine machine, long pressCycle)
    {
        if (pressCycle == long.MaxValue || machine.Cycles < pressCycle)
        {
            return false;
        }
        machine.SetKey(0x0f, 0x08, secondaryScanMask: null, pressed: true);
        Volatile.Write(ref _autoAnswerPressCycle, long.MaxValue);
        MessageEmitted?.Invoke("Incoming call automation: center down applied.");
        return true;
    }

    void TryReleaseAutoAnswer(MiaMachine machine)
    {
        long releaseCycle = Volatile.Read(ref _autoAnswerReleaseCycle);
        if (releaseCycle != long.MaxValue && machine.Cycles >= releaseCycle)
        {
            machine.SetKey(0x0f, 0x08, secondaryScanMask: null, pressed: false);
            Volatile.Write(ref _autoAnswerReleaseCycle, long.MaxValue);
            Volatile.Write(ref _autoAnswerIncomingCall, 0);
            MessageEmitted?.Invoke("Incoming call automation: center up applied.");
        }
    }

    static void ObserveRssiWrite(
        Cpu cpu,
        int address,
        Action<int> observeWriterPc)
    {
        var previous = cpu.WriteHooks[address];
        cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
        {
            var merged = (byte)((oldValue & ~mask) | (value & mask));
            if (merged != oldValue)
            {
                observeWriterPc(cpu.PC);
            }
            return previous?.Invoke(value, oldValue, hookAddress, mask) ?? false;
        };
    }

}
