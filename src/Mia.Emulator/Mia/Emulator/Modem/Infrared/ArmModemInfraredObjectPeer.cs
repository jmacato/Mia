// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using ObexHeaderDecode =
    (bool IsValid, byte Id, int ValueOffset, int ValueLength, int EncodedLength);
using ObexHeaderOutcome =
    (bool Success, bool Continue, bool EndOfBody, int ConsumedLength);
using ObexPacketDecode = (int State, byte[]? Packet);

namespace Mia.Emulator.Modem.Infrared;

/// <summary>
/// Deterministic IrDA Object Exchange peer. The handset still runs its native
/// IrLAP, IrLMP, IAS, TinyTP, and OBEX implementations; this class supplies
/// the other endpoint at the framed SIR boundary.
/// </summary>
internal sealed class ArmModemInfraredObjectPeer : IDisposable
{
    const byte BroadcastAddress = 0xfe;
    const byte CommandBit = 0x01;
    const byte PollFinalBit = 0x10;
    const byte XidCommand = 0x2f;
    const byte XidResponse = 0xaf;
    const byte SnrmCommand = 0x83;
    const byte UaResponse = 0x63;
    const byte DiscCommand = 0x43;
    const byte ReceiveReady = 0x01;
    const byte ControlBit = 0x80;
    const byte LmpConnectCommand = 0x01;
    const byte LmpConnectConfirm = 0x81;
    const byte LmpDisconnect = 0x02;
    const byte IasSelector = 0x00;
    const byte ObjectPushSelector = 0x04;
    const byte OutboundIasSelector = 0x10;
    const byte OutboundObjectPushSelector = 0x12;
    const int MaximumObexPacketLength = 220;
    const int MaximumReceivedObjectLength = 1_048_576;
    const long DeferredActionCycles = 250_000;
    const int InvalidObexPacket = -1;
    const int IncompleteObexPacket = 0;
    const int CompleteObexPacket = 1;

    static readonly byte[] LocalAddress = [0x44, 0x33, 0x22, 0x11];
    static readonly byte[] QosParameters =
    [
        0x01, 0x01, 0x02, // 9,600 bit/s SIR
        0x82, 0x01, 0x0f,
        0x83, 0x01, 0x07, // 64, 128, or 256-byte information field
        0x84, 0x01, 0x01, // one-frame window
        0x85, 0x01, 0xff,
        0x86, 0x01, 0xff,
        0x08, 0x01, 0xff,
    ];

    readonly ArmModemInfraredPeripheral _infrared;
    readonly byte[] _remoteAddress = new byte[4];
    readonly List<byte> _receivedObjectBytes = [];
    readonly List<byte> _obexReceiveBuffer = [];
    readonly Queue<byte[]> _deferredFrames = [];
    MiaTransferObject? _stagedObject;
    MiaTransferObject? _receivedObject;
    MiaInfraredTransferPhase _phase;
    ArmModemInfraredObjectPeerScheduledAction _scheduledAction;
    IDisposable? _scheduledActionEvent;
    IDisposable? _deferredFrameEvent;
    byte _connectionAddress = 0x22;
    byte _sendSequence;
    byte _receiveSequence;
    byte _remoteObjectPushSelector;
    byte _remoteIasClientSelector;
    int _outgoingObjectOffset;
    int _objectBytesTransferred;
    int? _receivedObjectExpectedLength;
    string _receivedObjectName = "received.bin";
    string _receivedObjectMediaType = "application/octet-stream";
    ArmModemInfraredObjectPeerLinkRole _role;
    bool _outgoingFinalPutSent;
    bool _outgoingHeadersSent;
    bool _outboundObexConnected;
    bool _outboundFirstPutPending;
    bool _linkDisconnectPending;
    bool _observingNativeFrame;
    bool _disposed;
    long _sentFrameSerial;
    string _message = "Infrared object exchange is idle.";

    public ArmModemInfraredObjectPeer(ArmModemInfraredPeripheral infrared)
    {
        _infrared = infrared;
        _infrared.FrameTransmitted += ObserveTransmittedFrame;
        _infrared.PortEnabledChanged += ObservePortEnabled;
    }

    public long ObjectsSentToHandset { get; private set; }

    public long ObjectsReceivedFromHandset { get; private set; }

    public bool Enabled { get; set; } = true;

    public event Action<MiaInfraredTransferStatus>? StatusChanged;

    public event Action<MiaTransferObject>? ObjectReceivedFromHandset;

    public MiaInfraredTransferStatus CaptureStatus() => new(
        _infrared.PortEnabled,
        _phase,
        _message,
        // Both objects are replaced, never mutated. Reusing their immutable
        // snapshots here avoids copying a potentially 1 MiB body for every
        // progress notification in a segmented transfer.
        _stagedObject,
        _receivedObject,
        _objectBytesTransferred,
        ObjectsSentToHandset,
        ObjectsReceivedFromHandset);

    public void StageObjectForHandset(MiaTransferObject? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stagedObject = value?.Snapshot();
        ResetLink();
        _objectBytesTransferred = 0;
        if (_stagedObject is null)
        {
            SetStatus(
                MiaInfraredTransferPhase.Idle,
                _infrared.PortEnabled
                    ? "Receive item is waiting for a nearby IrDA sender. " +
                        "Press NO on the handset to cancel."
                    : "Infrared object exchange is ready to receive " +
                        "from the handset.");
            return;
        }

        _outgoingObjectOffset = 0;
        _outgoingFinalPutSent = false;
        if (_infrared.PortEnabled)
        {
            Schedule(ArmModemInfraredObjectPeerScheduledAction.StartDiscovery);
            SetStatus(
                MiaInfraredTransferPhase.Discovering,
                $"Waiting to discover the handset for {_stagedObject.Value.Name}.");
        }
        else
        {
            SetStatus(
                MiaInfraredTransferPhase.WaitingForPort,
                $"Staged {_stagedObject.Value.Name}; open Connect → Receive item.");
        }
    }

    public MiaTransferObject? GetReceivedObject() =>
        _receivedObject?.Snapshot();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _infrared.FrameTransmitted -= ObserveTransmittedFrame;
        _infrared.PortEnabledChanged -= ObservePortEnabled;
        CancelScheduledEvents();
        _disposed = true;
    }

    void ObservePortEnabled(bool enabled)
    {
        if (!enabled)
        {
            ResetLink();
            SetStatus(
                _stagedObject is null
                    ? MiaInfraredTransferPhase.Idle
                    : MiaInfraredTransferPhase.WaitingForPort,
                _stagedObject is null
                    ? "Infrared port is off."
                    : $"Staged {_stagedObject.Value.Name}; open Connect → Receive item.");
            return;
        }

        if (_stagedObject is not null)
        {
            Schedule(ArmModemInfraredObjectPeerScheduledAction.StartDiscovery);
            SetStatus(
                MiaInfraredTransferPhase.Discovering,
                $"Discovering the handset for {_stagedObject.Value.Name}.");
        }
        else
        {
            SetStatus(
                MiaInfraredTransferPhase.Idle,
                "Receive item is waiting for a nearby IrDA sender. " +
                "Press NO on the handset to cancel.");
        }
    }

    void ObserveTransmittedFrame(ReadOnlyMemory<byte> frame)
    {
        _observingNativeFrame = true;
        try
        {
            ObserveFrame(frame.Span);
        }
        finally
        {
            _observingNativeFrame = false;
        }
    }

    internal void ObserveFrame(ReadOnlySpan<byte> frame)
    {
        if (_disposed || !Enabled || frame.Length < 2)
        {
            return;
        }

        switch (ClassifyFrame(frame))
        {
            case ArmModemInfraredFrameKind.Discovery:
                ObserveDiscoveryFrame(frame);
                break;
            case ArmModemInfraredFrameKind.SetNormalResponseMode:
                ObserveSnrm(frame);
                break;
            case ArmModemInfraredFrameKind.UnnumberedAcknowledgement:
                ObserveUa(frame);
                break;
            case ArmModemInfraredFrameKind.Disconnect:
                ObserveDisconnect();
                break;
            case ArmModemInfraredFrameKind.Information:
                ObserveInformationFrame(frame);
                break;
            case ArmModemInfraredFrameKind.ReceiveReady:
                ObserveReceiveReady();
                break;
        }
    }

    static ArmModemInfraredFrameKind ClassifyFrame(ReadOnlySpan<byte> frame)
    {
        byte control = frame[1];
        byte unpolledControl = (byte)(control & ~PollFinalBit);
        return (frame[0], control, unpolledControl) switch
        {
            (byte address, _, XidCommand or XidResponse)
                when (address & BroadcastAddress) == BroadcastAddress =>
                    ArmModemInfraredFrameKind.Discovery,
            (_, _, SnrmCommand) =>
                ArmModemInfraredFrameKind.SetNormalResponseMode,
            (_, _, UaResponse) =>
                ArmModemInfraredFrameKind.UnnumberedAcknowledgement,
            (_, _, DiscCommand) => ArmModemInfraredFrameKind.Disconnect,
            (_, byte value, _) when (value & 0x01) == 0 =>
                ArmModemInfraredFrameKind.Information,
            (_, byte value, _) when (value & 0x03) == 0x01 &&
                (value & 0x0f) == ReceiveReady =>
                    ArmModemInfraredFrameKind.ReceiveReady,
            _ => ArmModemInfraredFrameKind.Unknown,
        };
    }

    void ObserveDisconnect()
    {
        bool transferCompleted = _phase == MiaInfraredTransferPhase.Completed;
        SendUa(includeParameters: false);
        ResetLink();
        if (!transferCompleted)
        {
            SetStatus(
                MiaInfraredTransferPhase.Completed,
                "Infrared link disconnected cleanly.");
        }
    }

    void ObserveDiscoveryFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14 || frame[2] != 0x01)
        {
            return;
        }

        byte slot = frame[12];
        frame.Slice(3, 4).CopyTo(_remoteAddress);
        if ((frame[1] & ~PollFinalBit) == XidCommand)
        {
            RespondToDiscoveryCommand(frame[11], slot);
            return;
        }

        if (_phase != MiaInfraredTransferPhase.Discovering)
        {
            return;
        }

        SendDiscoveryCommand(final: true);
        _role = ArmModemInfraredObjectPeerLinkRole.Primary;
        SetStatus(
            MiaInfraredTransferPhase.ConnectingLink,
            "Handset discovered; establishing IrLAP.");
        Schedule(ArmModemInfraredObjectPeerScheduledAction.SendSnrm);
    }

    void RespondToDiscoveryCommand(byte flags, byte slot)
    {
        if (slot == 0)
        {
            SendDiscoveryResponse(flags, slot);
        }
    }

    void ObserveSnrm(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 11)
        {
            return;
        }

        frame.Slice(2, 4).CopyTo(_remoteAddress);
        _connectionAddress = (byte)(frame[10] & BroadcastAddress);
        _role = ArmModemInfraredObjectPeerLinkRole.Secondary;
        _sendSequence = 0;
        _receiveSequence = 0;
        SendUa(includeParameters: true);
        SetStatus(
            MiaInfraredTransferPhase.ConnectingObjectPush,
            "IrLAP connected; advertising IAS and OBEX Object Push.");
    }

    void ObserveUa(ReadOnlySpan<byte> frame)
    {
        if (_role != ArmModemInfraredObjectPeerLinkRole.Primary ||
            _phase != MiaInfraredTransferPhase.ConnectingLink ||
            frame.Length < 10)
        {
            return;
        }

        _sendSequence = 0;
        _receiveSequence = 0;
        SetStatus(
            MiaInfraredTransferPhase.QueryingServices,
            "IrLAP connected; querying the handset OBEX service.");
        SendLmpControl(
            IasSelector,
            OutboundIasSelector,
            LmpConnectCommand,
            []);
    }

    void ObserveInformationFrame(ReadOnlySpan<byte> frame)
    {
        byte control = frame[1];
        byte sequence = (byte)((control >> 1) & 0x07);
        if (sequence != _receiveSequence)
        {
            SendReceiveReady(command: _role == ArmModemInfraredObjectPeerLinkRole.Primary);
            return;
        }

        _receiveSequence = (byte)((_receiveSequence + 1) & 0x07);
        long transmittedBefore = _sentFrameSerial;
        if (frame.Length > 2)
        {
            ObserveLmp(frame[2..]);
        }

        AcknowledgeInformationFrame(transmittedBefore);
    }

    void AcknowledgeInformationFrame(long transmittedBefore)
    {
        if (_sentFrameSerial != transmittedBefore)
        {
            return;
        }
        if (_role == ArmModemInfraredObjectPeerLinkRole.Secondary)
        {
            SendReceiveReady(command: false);
        }
        else
        {
            Schedule(ArmModemInfraredObjectPeerScheduledAction.Poll);
        }
    }

    void ObserveReceiveReady()
    {
        switch ((_role, _phase))
        {
            case (ArmModemInfraredObjectPeerLinkRole.Secondary, _):
                SendReceiveReady(command: false);
                break;
            case (_, MiaInfraredTransferPhase.Completed):
                DisconnectCompletedLinkIfPending();
                break;
            case (_, MiaInfraredTransferPhase.Idle or MiaInfraredTransferPhase.Failed):
                break;
            case (_, MiaInfraredTransferPhase.QueryingServices)
                when _remoteObjectPushSelector != 0:
                ConnectOutboundObjectPush();
                break;
            default:
                Schedule(ArmModemInfraredObjectPeerScheduledAction.Poll);
                break;
        }
    }

    void DisconnectCompletedLinkIfPending()
    {
        if (!_linkDisconnectPending)
        {
            return;
        }

        _linkDisconnectPending = false;
        SendFrame(
        [
            (byte)(_connectionAddress | CommandBit),
            (byte)(DiscCommand | PollFinalBit),
        ]);
    }

    void ConnectOutboundObjectPush()
    {
        SetStatus(
            MiaInfraredTransferPhase.ConnectingObjectPush,
            "Connecting TinyTP to the handset Object Push service.");
        SendLmpControl(
            _remoteObjectPushSelector,
            OutboundObjectPushSelector,
            LmpConnectCommand,
            [0x10]);
    }

    void ObserveLmp(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 2)
        {
            return;
        }

        if ((pdu[0] & ControlBit) != 0)
        {
            ObserveLmpControl(pdu);
            return;
        }

        ObserveLmpData(pdu);
    }

    void ObserveLmpControl(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 4)
        {
            return;
        }

        byte destination = (byte)(pdu[0] & 0x7f);
        byte source = (byte)(pdu[1] & 0x7f);
        switch (pdu[2])
        {
            case LmpConnectCommand:
                ObserveLmpConnectCommand(destination, source, pdu[4..]);
                break;
            case LmpConnectConfirm:
                ObserveLmpConnectConfirm(source, pdu[4..]);
                break;
            case LmpDisconnect:
                ObserveLmpDisconnect(destination, source);
                break;
        }
    }

    void ObserveLmpData(ReadOnlySpan<byte> pdu)
    {
        byte destination = (byte)(pdu[0] & 0x7f);
        byte source = (byte)(pdu[1] & 0x7f);
        ReadOnlySpan<byte> data = pdu[2..];
        switch (destination)
        {
            case IasSelector:
                ObserveIasRequest(source, data);
                break;
            case OutboundIasSelector:
                ObserveIasResponse(data);
                break;
            case OutboundObjectPushSelector:
                ObserveOutboundTinyTp(data);
                break;
            case ObjectPushSelector:
                ObserveIncomingTinyTp(source, data);
                break;
        }
    }

    void ObserveLmpConnectCommand(
        byte destination,
        byte source,
        ReadOnlySpan<byte> userData)
    {
        if (destination == IasSelector)
        {
            _remoteIasClientSelector = source;
            SendLmpControl(
                source,
                IasSelector,
                LmpConnectConfirm,
                []);
            return;
        }
        if (destination == ObjectPushSelector)
        {
            _receivedObjectBytes.Clear();
            _obexReceiveBuffer.Clear();
            _receivedObject = null;
            _objectBytesTransferred = 0;
            _receivedObjectExpectedLength = null;
            _receivedObjectName = "received.bin";
            _receivedObjectMediaType = "application/octet-stream";
            SetStatus(
                MiaInfraredTransferPhase.ReceivingObject,
                "TinyTP connected; receiving an object from the handset.");
            SendLmpControl(
                source,
                ObjectPushSelector,
                LmpConnectConfirm,
                [0x19]);
        }
    }

    void ObserveLmpConnectConfirm(byte source, ReadOnlySpan<byte> userData)
    {
        if (source == IasSelector &&
            _phase == MiaInfraredTransferPhase.QueryingServices)
        {
            SendIasQuery("IrDA:OBEX", "IrDA:TinyTP:LsapSel");
            return;
        }
        if (source == _remoteObjectPushSelector &&
            _phase == MiaInfraredTransferPhase.ConnectingObjectPush)
        {
            SendTinyTp(
                source,
                OutboundObjectPushSelector,
                [0x80, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00]);
        }
    }

    void ObserveLmpDisconnect(byte destination, byte source)
    {
        if (destination != IasSelector ||
            source != _remoteIasClientSelector)
        {
            return;
        }

        _remoteIasClientSelector = 0;
        if (_outboundObexConnected && _outboundFirstPutPending)
        {
            _outboundFirstPutPending = false;
            SendNextObexPut();
        }
    }

    void ObserveIasRequest(byte source, ReadOnlySpan<byte> data)
    {
        ArmModemInfraredIasQuery? query = ParseIasQuery(data);
        if (query is null)
        {
            return;
        }

        switch (query.Value.ClassName, query.Value.Attribute)
        {
            case ("Device", "DeviceName"):
                SendIasStringResponse(source, "Codex IrDA");
                break;
            case ("OBEX" or "OBEX:IrXfer" or "IrDA:OBEX", "IrDA:TinyTP:LsapSel"):
                SendIasIntegerResponse(source, ObjectPushSelector);
                break;
            default:
                SendIasMissingResponse(source);
                break;
        }
    }

    static ArmModemInfraredIasQuery? ParseIasQuery(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != 0x84)
        {
            return null;
        }

        int classLength = data[1];
        if (data.Length < 3 + classLength)
        {
            return null;
        }

        int attributeLengthOffset = 2 + classLength;
        int attributeLength = data[attributeLengthOffset];
        if (data.Length < attributeLengthOffset + 1 + attributeLength)
        {
            return null;
        }

        return new(
            Encoding.ASCII.GetString(data.Slice(2, classLength)),
            Encoding.ASCII.GetString(
                data.Slice(attributeLengthOffset + 1, attributeLength)));
    }

    void ObserveIasResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 11 || data[0] != 0x84 || data[1] != 0x00 ||
            data[6] != 0x01)
        {
            Fail("The handset returned an invalid OBEX IAS response.");
            return;
        }

        uint selector = BinaryPrimitives.ReadUInt32BigEndian(data[7..]);
        if (selector is 0 or > 0x6f)
        {
            Fail("The handset returned an invalid OBEX LSAP selector.");
            return;
        }
        _remoteObjectPushSelector = (byte)selector;
        SendLmpControl(
            IasSelector,
            OutboundIasSelector,
            LmpDisconnect,
            []);
    }

    void ObserveOutboundTinyTp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }
        ObserveOutboundObex(data[1..]);
    }

    void ObserveOutboundObex(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 3)
        {
            return;
        }
        byte response = packet[0];
        switch (_phase)
        {
            case MiaInfraredTransferPhase.ConnectingObjectPush
                when response == 0xa0 && packet.Length >= 7:
                BeginOutboundObexTransfer();
                break;
            case MiaInfraredTransferPhase.SendingObject:
                ObserveOutboundPutResponse(response);
                break;
            case MiaInfraredTransferPhase.Disconnecting when response == 0xa0:
                CompleteOutboundObexTransfer();
                break;
        }
    }

    void BeginOutboundObexTransfer()
    {
        _outgoingObjectOffset = 0;
        _objectBytesTransferred = 0;
        _outgoingFinalPutSent = false;
        _outgoingHeadersSent = false;
        _outboundObexConnected = true;
        SetStatus(
            MiaInfraredTransferPhase.SendingObject,
            $"Sending {_stagedObject?.Name ?? "object"} via IrDA.");
        if (_remoteIasClientSelector == 0)
        {
            SendNextObexPut();
        }
        else
        {
            _outboundFirstPutPending = true;
        }
    }

    void ObserveOutboundPutResponse(byte response)
    {
        switch ((response, _outgoingFinalPutSent))
        {
            case (0x90, false):
                SetStatus(
                    MiaInfraredTransferPhase.SendingObject,
                    $"Sending {_stagedObject?.Name ?? "object"} via IrDA · " +
                    $"{_objectBytesTransferred:n0} B transferred.");
                SendNextObexPut();
                break;
            case (0xa0, true):
                SetStatus(
                    MiaInfraredTransferPhase.Disconnecting,
                    "Object accepted; closing the OBEX session.");
                SendTinyTp(
                    _remoteObjectPushSelector,
                    OutboundObjectPushSelector,
                    [0x81, 0x00, 0x03]);
                break;
        }
    }

    void CompleteOutboundObexTransfer()
    {
        ObjectsSentToHandset++;
        SetStatus(
            MiaInfraredTransferPhase.Completed,
            $"Sent {_stagedObject?.Name ?? "object"} to the handset via IrDA.");
        _linkDisconnectPending = true;
        SendLmpControl(
            _remoteObjectPushSelector,
            OutboundObjectPushSelector,
            LmpDisconnect,
            []);
    }

    void ObserveIncomingTinyTp(byte source, ReadOnlySpan<byte> data)
    {
        if (data.Length <= 1)
        {
            return;
        }

        _obexReceiveBuffer.AddRange(data[1..].ToArray());
        bool sentResponse = false;
        ObexPacketDecode decoded = DecodeIncomingObexPacket();
        while (decoded.State == CompleteObexPacket)
        {
            sentResponse |= RespondToIncomingObexPacket(source, decoded.Packet!);
            decoded = DecodeIncomingObexPacket();
        }

        FinishIncomingObexBatch(source, decoded.State, sentResponse);
    }

    ObexPacketDecode DecodeIncomingObexPacket()
    {
        if (_obexReceiveBuffer.Count < 3)
        {
            return (IncompleteObexPacket, null);
        }

        int length = (_obexReceiveBuffer[1] << 8) |
            _obexReceiveBuffer[2];
        if (length is < 3 or > ushort.MaxValue)
        {
            return (InvalidObexPacket, null);
        }

        if (_obexReceiveBuffer.Count < length)
        {
            return (IncompleteObexPacket, null);
        }

        byte[] packet = _obexReceiveBuffer.GetRange(0, length).ToArray();
        _obexReceiveBuffer.RemoveRange(0, length);
        return (CompleteObexPacket, packet);
    }

    bool RespondToIncomingObexPacket(byte source, byte[] packet)
    {
        byte[]? response = ObserveIncomingObexPacket(packet);
        if (response is null)
        {
            return false;
        }

        SendTinyTp(source, ObjectPushSelector, response);
        return true;
    }

    void FinishIncomingObexBatch(
        byte source,
        int state,
        bool sentResponse)
    {
        switch ((state, sentResponse))
        {
            case (InvalidObexPacket, _):
                _obexReceiveBuffer.Clear();
                break;
            case (IncompleteObexPacket, false):
                SendTinyTpCredit(source);
                break;
        }
    }

    byte[]? ObserveIncomingObexPacket(ReadOnlySpan<byte> packet)
    {
        switch (packet[0])
        {
            case 0x80 when packet.Length >= 7:
                return BeginIncomingObexTransfer();
            case 0x02:
            case 0x82:
                return ObserveIncomingObexPut(packet[3..]);
            case 0x81:
            case 0xff:
                return [0xa0, 0x00, 0x03];
            default:
                return null;
        }
    }

    byte[] BeginIncomingObexTransfer()
    {
        _receivedObjectBytes.Clear();
        _receivedObject = null;
        _objectBytesTransferred = 0;
        _receivedObjectExpectedLength = null;
        _receivedObjectName = "received.bin";
        _receivedObjectMediaType = "application/octet-stream";
        return [0xa0, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00];
    }

    byte[] ObserveIncomingObexPut(ReadOnlySpan<byte> headers)
    {
        if (!ObserveObexHeaders(headers))
        {
            return [0x90, 0x00, 0x03];
        }

        if (_receivedObjectExpectedLength is int expectedLength &&
            expectedLength != _receivedObjectBytes.Count)
        {
            Fail(
                $"IrDA object ended at {_receivedObjectBytes.Count:n0} B, " +
                $"but its OBEX Length header declared {expectedLength:n0} B.");
            return [0xc0, 0x00, 0x03];
        }

        CompleteIncomingObexTransfer();
        return [0xa0, 0x00, 0x03];
    }

    void CompleteIncomingObexTransfer()
    {
        var received = new MiaTransferObject(
            _receivedObjectName,
            _receivedObjectMediaType,
            _receivedObjectBytes.ToArray());
        _receivedObject = received;
        ObjectsReceivedFromHandset++;
        SetStatus(
            MiaInfraredTransferPhase.Completed,
            $"Received {received.Name} from the handset via IrDA.");
        ObjectReceivedFromHandset?.Invoke(received.Snapshot());
    }

    bool ObserveObexHeaders(ReadOnlySpan<byte> headers)
    {
        bool endOfBody = false;
        ObexHeaderOutcome outcome =
            (Success: true, Continue: true, EndOfBody: false, ConsumedLength: 0);
        while (!headers.IsEmpty && outcome.Continue)
        {
            ObexHeaderDecode header = DecodeObexHeader(headers);
            outcome = ApplyObexHeader(headers, header);
            endOfBody |= outcome.EndOfBody;
            headers = headers[outcome.ConsumedLength..];
        }
        return outcome.Success && endOfBody;
    }

    static ObexHeaderDecode DecodeObexHeader(
        ReadOnlySpan<byte> encoded) =>
        (encoded[0] & 0xc0) switch
        {
            0x00 or 0x40 => DecodeLengthPrefixedObexHeader(encoded),
            0x80 => DecodeFixedObexHeader(encoded, 2),
            _ => DecodeFixedObexHeader(encoded, 5),
        };

    static ObexHeaderDecode DecodeLengthPrefixedObexHeader(
        ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 3)
        {
            return default;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(encoded[1..]);
        return length >= 3 && length <= encoded.Length
            ? (true, encoded[0], 3, length - 3, length)
            : default;
    }

    static ObexHeaderDecode DecodeFixedObexHeader(
        ReadOnlySpan<byte> encoded,
        int length) =>
        encoded.Length >= length
            ? (true, encoded[0], 1, length - 1, length)
            : default;

    ObexHeaderOutcome ApplyObexHeader(
        ReadOnlySpan<byte> encoded,
        ObexHeaderDecode header)
    {
        if (!header.IsValid)
        {
            return (true, false, false, 0);
        }

        return header.Id switch
        {
            0x48 => ApplyObexBodyHeader(encoded, header, endOfBody: false),
            0x49 => ApplyObexBodyHeader(encoded, header, endOfBody: true),
            0x01 => ApplyObexNameHeader(encoded, header),
            0x42 => ApplyObexTypeHeader(encoded, header),
            0xc3 => ApplyObexLengthHeader(encoded, header),
            _ => NextObexHeader(header.EncodedLength),
        };
    }

    ObexHeaderOutcome ApplyObexBodyHeader(
        ReadOnlySpan<byte> encoded,
        ObexHeaderDecode header,
        bool endOfBody)
    {
        _receivedObjectBytes.AddRange(
            encoded.Slice(header.ValueOffset, header.ValueLength).ToArray());
        if (_receivedObjectBytes.Count > MaximumReceivedObjectLength)
        {
            Fail("The received IrDA object exceeds the 1 MiB limit.");
            return (false, false, false, 0);
        }

        _objectBytesTransferred = _receivedObjectBytes.Count;
        StatusChanged?.Invoke(CaptureStatus());
        return NextObexHeader(header.EncodedLength, endOfBody);
    }

    ObexHeaderOutcome ApplyObexNameHeader(
        ReadOnlySpan<byte> encoded,
        ObexHeaderDecode header)
    {
        ReadOnlySpan<byte> value = TrimUnicodeTerminator(
            encoded.Slice(header.ValueOffset, header.ValueLength));
        if (value.Length % 2 == 0)
        {
            _receivedObjectName = Encoding.BigEndianUnicode.GetString(value);
            _receivedObjectMediaType = GuessMediaType(_receivedObjectName);
        }
        return NextObexHeader(header.EncodedLength);
    }

    static ReadOnlySpan<byte> TrimUnicodeTerminator(ReadOnlySpan<byte> value) =>
        value.Length >= 2 && value[^2] == 0 && value[^1] == 0
            ? value[..^2]
            : value;

    ObexHeaderOutcome ApplyObexTypeHeader(
        ReadOnlySpan<byte> encoded,
        ObexHeaderDecode header)
    {
        ReadOnlySpan<byte> headerValue =
            encoded.Slice(header.ValueOffset, header.ValueLength);
        int terminator = headerValue.IndexOf((byte)0);
        ReadOnlySpan<byte> value = terminator >= 0
            ? headerValue[..terminator]
            : headerValue;
        _receivedObjectMediaType = Encoding.ASCII.GetString(value);
        return NextObexHeader(header.EncodedLength);
    }

    ObexHeaderOutcome ApplyObexLengthHeader(
        ReadOnlySpan<byte> encoded,
        ObexHeaderDecode header)
    {
        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(
            encoded.Slice(header.ValueOffset, header.ValueLength));
        if (declaredLength > MaximumReceivedObjectLength)
        {
            Fail(
                "The received IrDA object's OBEX Length header " +
                "exceeds the 1 MiB limit.");
            return (false, false, false, 0);
        }

        _receivedObjectExpectedLength = (int)declaredLength;
        return NextObexHeader(header.EncodedLength);
    }

    static ObexHeaderOutcome NextObexHeader(
        int consumedLength,
        bool endOfBody = false) =>
        (true, true, endOfBody, consumedLength);

    void SendNextObexPut()
    {
        if (_stagedObject is not { } staged)
        {
            Fail("No object is staged for the IrDA transfer.");
            return;
        }

        ArmModemInfraredObexHeaders headers = CreateOutgoingObexHeaders(staged);
        if (TrySendMetadataOnly(staged, headers))
        {
            return;
        }

        SendNextObexBody(staged, headers);
    }

    ArmModemInfraredObexHeaders CreateOutgoingObexHeaders(MiaTransferObject staged)
    {
        bool include = _outgoingObjectOffset == 0 && !_outgoingHeadersSent;
        byte[] name = include
            ? CreateUnicodeHeader(0x01, staged.Name)
            : [];
        byte[] type = include
            ? CreateByteSequenceHeader(
                0x42,
                Encoding.ASCII.GetBytes(staged.MediaType + "\0"))
            : [];
        byte[] length = include
            ?
            [
                0xc3,
                (byte)(staged.Data.Length >> 24),
                (byte)(staged.Data.Length >> 16),
                (byte)(staged.Data.Length >> 8),
                (byte)staged.Data.Length,
            ]
            : [];
        return new(include, name, type, length);
    }

    bool TrySendMetadataOnly(
        MiaTransferObject staged,
        ArmModemInfraredObexHeaders headers)
    {
        if (!headers.Include ||
            headers.PacketOverhead + 3 + staged.Data.Length <= MaximumObexPacketLength)
        {
            return false;
        }

        byte[] metadata = CreateMetadataPacket(headers);
        _outgoingHeadersSent = true;
        _outgoingFinalPutSent = false;
        SendTinyTp(
            _remoteObjectPushSelector,
            OutboundObjectPushSelector,
            metadata);
        SetWaitingForHandsetConfirmation(staged.Name);
        return true;
    }

    static byte[] CreateMetadataPacket(ArmModemInfraredObexHeaders headers)
    {
        var metadata = new byte[headers.PacketOverhead];
        metadata[0] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(
            metadata.AsSpan(1),
            checked((ushort)metadata.Length));
        headers.CopyTo(metadata.AsSpan(3));
        return metadata;
    }

    void SendNextObexBody(
        MiaTransferObject staged,
        ArmModemInfraredObexHeaders headers)
    {
        int available = MaximumObexPacketLength - headers.PacketOverhead - 3;
        if (available < 0)
        {
            Fail("The IrDA object's name and media type are too long.");
            return;
        }
        int bodyLength = Math.Min(
            available,
            staged.Data.Length - _outgoingObjectOffset);
        bool final =
            _outgoingObjectOffset + bodyLength == staged.Data.Length;
        var packet = new byte[
            headers.PacketOverhead + 3 + bodyLength];
        packet[0] = final ? (byte)0x82 : (byte)0x02;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(1),
            checked((ushort)packet.Length));
        headers.CopyTo(packet.AsSpan(3));
        int bodyOffset = headers.PacketOverhead;
        packet[bodyOffset] = final ? (byte)0x49 : (byte)0x48;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(bodyOffset + 1),
            checked((ushort)(bodyLength + 3)));
        staged.Data.Span.Slice(_outgoingObjectOffset, bodyLength)
            .CopyTo(packet.AsSpan(bodyOffset + 3));
        _outgoingObjectOffset += bodyLength;
        _objectBytesTransferred = _outgoingObjectOffset;
        _outgoingHeadersSent = true;
        _outgoingFinalPutSent = final;
        SendTinyTp(
            _remoteObjectPushSelector,
            OutboundObjectPushSelector,
            packet);
        if (headers.Include)
        {
            SetWaitingForHandsetConfirmation(staged.Name);
        }
    }

    void SendIasQuery(string className, string attribute)
    {
        byte[] classBytes = Encoding.ASCII.GetBytes(className);
        byte[] attributeBytes = Encoding.ASCII.GetBytes(attribute);
        var request = new byte[3 + classBytes.Length + attributeBytes.Length];
        request[0] = 0x84;
        request[1] = checked((byte)classBytes.Length);
        classBytes.CopyTo(request.AsSpan(2));
        request[2 + classBytes.Length] =
            checked((byte)attributeBytes.Length);
        attributeBytes.CopyTo(request.AsSpan(3 + classBytes.Length));
        SendLmpData(IasSelector, OutboundIasSelector, request);
    }

    void SendIasIntegerResponse(byte destination, byte value)
    {
        SendLmpData(
            destination,
            IasSelector,
            [0x84, 0x00, 0x00, 0x01, 0x00, 0x01,
             0x01, 0x00, 0x00, 0x00, value]);
    }

    void SendIasStringResponse(byte destination, string value)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(value);
        var response = new byte[9 + encoded.Length];
        response[0] = 0x84;
        response[1] = 0x00;
        response[2] = 0x00;
        response[3] = 0x01;
        response[4] = 0x00;
        response[5] = 0x01;
        response[6] = 0x03;
        response[7] = 0x00;
        response[8] = checked((byte)encoded.Length);
        encoded.CopyTo(response.AsSpan(9));
        SendLmpData(destination, IasSelector, response);
    }

    void SendIasMissingResponse(byte destination) =>
        SendLmpData(
            destination,
            IasSelector,
            [0x84, 0x01, 0x00, 0x00, 0x00]);

    void SendTinyTp(byte destination, byte source, ReadOnlySpan<byte> obex)
    {
        var data = new byte[1 + obex.Length];
        data[0] = 0x01;
        obex.CopyTo(data.AsSpan(1));
        SendLmpData(destination, source, data);
    }

    void SendTinyTpCredit(byte destination) =>
        SendLmpData(destination, ObjectPushSelector, [0x01]);

    void SetWaitingForHandsetConfirmation(string name) =>
        SetStatus(
            MiaInfraredTransferPhase.SendingObject,
            $"Incoming-item prompt shown for {name}. " +
            "Press YES on the handset to continue.");

    void SendLmpData(
        byte destination,
        byte source,
        ReadOnlySpan<byte> data)
    {
        var pdu = new byte[2 + data.Length];
        pdu[0] = destination;
        pdu[1] = source;
        data.CopyTo(pdu.AsSpan(2));
        SendInformation(pdu);
    }

    void SendLmpControl(
        byte destination,
        byte source,
        byte opcode,
        ReadOnlySpan<byte> userData)
    {
        var pdu = new byte[4 + userData.Length];
        pdu[0] = (byte)(destination | ControlBit);
        pdu[1] = source;
        pdu[2] = opcode;
        pdu[3] = opcode == LmpDisconnect ? (byte)0x01 : (byte)0x00;
        userData.CopyTo(pdu.AsSpan(4));
        SendInformation(pdu);
    }

    void SendInformation(ReadOnlySpan<byte> information)
    {
        bool command = _role == ArmModemInfraredObjectPeerLinkRole.Primary;
        var frame = new byte[2 + information.Length];
        frame[0] = (byte)(_connectionAddress |
            (command ? CommandBit : 0));
        frame[1] = (byte)(
            (_sendSequence << 1) |
            (_receiveSequence << 5) |
            PollFinalBit);
        information.CopyTo(frame.AsSpan(2));
        _sendSequence = (byte)((_sendSequence + 1) & 0x07);
        SendFrame(frame);
    }

    void SendReceiveReady(bool command)
    {
        SendFrame(
        [
            (byte)(_connectionAddress | (command ? CommandBit : 0)),
            (byte)(ReceiveReady |
                (_receiveSequence << 5) |
                PollFinalBit),
        ]);
    }

    void SendDiscoveryCommand(bool final)
    {
        var frame = new List<byte>
        {
            (byte)(BroadcastAddress | CommandBit),
            (byte)(XidCommand | PollFinalBit),
            0x01,
        };
        frame.AddRange(LocalAddress);
        frame.AddRange([0xff, 0xff, 0xff, 0xff]);
        frame.Add(0x00);
        frame.Add(final ? (byte)0xff : (byte)0x00);
        frame.Add(0x00);
        if (final)
        {
            frame.AddRange(
            [
                0x80, 0x20, 0x00,
                (byte)'C', (byte)'o', (byte)'d', (byte)'e', (byte)'x',
            ]);
        }
        SendFrame(frame.ToArray());
    }

    void SendDiscoveryResponse(byte flags, byte slot)
    {
        var frame = new List<byte>
        {
            BroadcastAddress,
            (byte)(XidResponse | PollFinalBit),
            0x01,
        };
        frame.AddRange(LocalAddress);
        frame.AddRange(_remoteAddress);
        frame.Add(flags);
        frame.Add(slot);
        frame.Add(0x00);
        frame.AddRange(
        [
            0x80, 0x20, 0x00,
            (byte)'C', (byte)'o', (byte)'d', (byte)'e', (byte)'x',
        ]);
        SendFrame(frame.ToArray());
    }

    void SendSnrm()
    {
        var frame = new List<byte>
        {
            (byte)(BroadcastAddress | CommandBit),
            (byte)(SnrmCommand | PollFinalBit),
        };
        frame.AddRange(LocalAddress);
        frame.AddRange(_remoteAddress);
        frame.Add(_connectionAddress);
        frame.AddRange(QosParameters);
        SendFrame(frame.ToArray());
    }

    void SendUa(bool includeParameters)
    {
        var frame = new List<byte>
        {
            _connectionAddress,
            (byte)(UaResponse | PollFinalBit),
        };
        if (includeParameters)
        {
            frame.AddRange(LocalAddress);
            frame.AddRange(_remoteAddress);
            frame.AddRange(QosParameters);
        }
        SendFrame(frame.ToArray());
    }

    void SendFrame(ReadOnlySpan<byte> frame)
    {
        _sentFrameSerial++;
        if (_observingNativeFrame)
        {
            QueueDeferredFrame(frame);
            return;
        }
        _infrared.QueueReceivedFrame(frame);
    }

    void QueueDeferredFrame(ReadOnlySpan<byte> frame)
    {
        _deferredFrames.Enqueue(frame.ToArray());
        if (_deferredFrames.Count == 1)
        {
            ScheduleDeferredFrame();
        }
    }

    void Schedule(ArmModemInfraredObjectPeerScheduledAction action)
    {
        _scheduledActionEvent?.Dispose();
        _scheduledAction = action;
        _scheduledActionEvent = _infrared.ScheduleEvent(
            _infrared.Cycles + DeferredActionCycles,
            _ =>
            {
                _scheduledActionEvent = null;
                if (_disposed || _scheduledAction != action)
                {
                    return;
                }

                _scheduledAction = ArmModemInfraredObjectPeerScheduledAction.None;
                switch (action)
                {
                    case ArmModemInfraredObjectPeerScheduledAction.StartDiscovery:
                        if (_infrared.PortEnabled && _stagedObject is not null)
                        {
                            SendDiscoveryCommand(final: false);
                        }
                        break;
                    case ArmModemInfraredObjectPeerScheduledAction.SendSnrm:
                        SendSnrm();
                        break;
                    case ArmModemInfraredObjectPeerScheduledAction.Poll:
                        SendReceiveReady(command: true);
                        break;
                }
            });
    }

    void ScheduleDeferredFrame()
    {
        _deferredFrameEvent?.Dispose();
        _deferredFrameEvent = _infrared.ScheduleEvent(
            _infrared.Cycles + DeferredActionCycles,
            _ =>
            {
                _deferredFrameEvent = null;
                if (_disposed || !_deferredFrames.TryDequeue(out byte[]? frame))
                {
                    return;
                }

                _infrared.QueueReceivedFrame(frame);
                if (_deferredFrames.Count != 0)
                {
                    ScheduleDeferredFrame();
                }
            });
    }

    void SetStatus(MiaInfraredTransferPhase phase, string message)
    {
        _phase = phase;
        _message = message;
        StatusChanged?.Invoke(CaptureStatus());
    }

    void Fail(string message)
    {
        CancelScheduledEvents();
        SetStatus(MiaInfraredTransferPhase.Failed, message);
    }

    void ResetLink()
    {
        CancelScheduledEvents();
        _role = ArmModemInfraredObjectPeerLinkRole.None;
        _sendSequence = 0;
        _receiveSequence = 0;
        _remoteObjectPushSelector = 0;
        _remoteIasClientSelector = 0;
        _outboundObexConnected = false;
        _outboundFirstPutPending = false;
        _outgoingHeadersSent = false;
        _linkDisconnectPending = false;
        _connectionAddress = 0x22;
        _obexReceiveBuffer.Clear();
    }

    void CancelScheduledEvents()
    {
        _scheduledAction = ArmModemInfraredObjectPeerScheduledAction.None;
        _scheduledActionEvent?.Dispose();
        _scheduledActionEvent = null;
        _deferredFrameEvent?.Dispose();
        _deferredFrameEvent = null;
        _deferredFrames.Clear();
    }

    static byte[] CreateUnicodeHeader(byte id, string value)
    {
        byte[] encoded = Encoding.BigEndianUnicode.GetBytes(value + "\0");
        var header = new byte[3 + encoded.Length];
        header[0] = id;
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(1),
            checked((ushort)header.Length));
        encoded.CopyTo(header.AsSpan(3));
        return header;
    }

    static byte[] CreateByteSequenceHeader(
        byte id,
        ReadOnlySpan<byte> value)
    {
        var header = new byte[3 + value.Length];
        header[0] = id;
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(1),
            checked((ushort)header.Length));
        value.CopyTo(header.AsSpan(3));
        return header;
    }

    static string GuessMediaType(string name) =>
        Path.GetExtension(name).ToUpperInvariant() switch
        {
            ".VCF" => "TEXT/X-VCARD",
            ".VCS" => "TEXT/X-VCALENDAR",
            ".TXT" => "text/plain",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".GIF" => "image/gif",
            ".PNG" => "image/png",
            ".MID" or ".MIDI" => "audio/midi",
            _ => "application/octet-stream",
        };
}
