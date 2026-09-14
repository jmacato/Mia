// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace Mia.Emulator.Bluetooth;

/// <summary>
/// Remote peer side of the native handset's L2CAP data channel.
///
/// The modem firmware emits the first 17 bytes of an L2CAP basic frame as DSP
/// transfer kind six and any remaining 17-byte pieces as kind five. This
/// controller reassembles that measured transport and returns protocol
/// responses through the same boundary; it does not synthesize handset UI
/// state or use a host Bluetooth stack.
/// </summary>
internal sealed class BluetoothL2capController
{
    internal const byte InitialTransferKind = 6;
    internal const byte ContinuationTransferKind = 5;
    internal const int MaximumTransferPayloadLength = 17;
    internal const ushort SignalingChannelId = 0x0001;
    internal const ushort SdpPsm = 0x0001;
    internal const ushort RfcommPsm = 0x0003;
    internal const ushort PeerSdpChannelId = 0x0041;
    internal const ushort PeerRfcommChannelId = 0x0042;
    internal const byte ObjectPushRfcommChannel = 9;
    internal const byte LocalObjectPushRfcommChannel = 10;
    internal const byte LocalObjectPushDlci =
        LocalObjectPushRfcommChannel * 2 + 1;
    const int MaximumIncomingObexPacketLength = 220;
    const int MaximumReceivedObjectLength = 1_048_576;

    static ReadOnlySpan<byte> ObexConnectSuccessResponse =>
        [0xa0, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00];

    static ReadOnlySpan<byte> ObexContinueResponse => [0x90, 0x00, 0x03];

    static ReadOnlySpan<byte> ObexSuccessResponse => [0xa0, 0x00, 0x03];

    readonly bool _initiateIncomingObjectPush;
    readonly MiaTransferObject _incomingObject;
    ushort _handsetSdpChannelId;
    ushort _handsetRfcommChannelId;
    byte _nextPeerIdentifier = 1;
    byte[]? _outboundPacket;
    int _outboundPacketLength;
    readonly List<byte> _obexReceiveBuffer = [];
    readonly List<byte> _receivedObject = [];
    string _receivedObjectName = "received.bin";
    string _receivedObjectMediaType = "application/octet-stream";
    int _incomingObjectOffset;
    int? _receivedObjectExpectedLength;
    bool _hasReceivedObject;
    bool _rfcommMultiplexerOpen;
    bool _rfcommObjectPushLinkOpen;
    BluetoothL2capControllerIncomingObjectPushPhase _incomingObjectPushPhase;

    internal BluetoothL2capController(
        bool initiateIncomingObjectPush = false,
        MiaTransferObject? incomingObject = null)
    {
        _initiateIncomingObjectPush = initiateIncomingObjectPush;
        _incomingObject = (incomingObject ?? CreateDefaultIncomingObject()).Snapshot();
        _incomingObjectPushPhase = initiateIncomingObjectPush
            ? BluetoothL2capControllerIncomingObjectPushPhase.AwaitOutboundFinalPut
            : BluetoothL2capControllerIncomingObjectPushPhase.Disabled;
    }

    internal long SdpServiceSearchAttributeRequestCount { get; private set; }

    internal long RfcommMultiplexerEstablishedCount { get; private set; }

    internal long RfcommObjectPushLinkEstablishedCount { get; private set; }

    internal long ObexConnectRequestCount { get; private set; }

    internal long ObexPutRequestCount { get; private set; }

    internal long ObexFinalPutRequestCount { get; private set; }

    internal long ObexDisconnectRequestCount { get; private set; }

    internal long ObexTransferredObjectByteCount => _receivedObject.Count;

    internal ReadOnlyMemory<byte> ObexTransferredObject =>
        _receivedObject.ToArray();

    internal MiaTransferObject? ObexTransferredObjectSnapshot =>
        !_hasReceivedObject
            ? null
            : new MiaTransferObject(
                _receivedObjectName,
                _receivedObjectMediaType,
                _receivedObject.ToArray());

    internal long IncomingObjectPushStartedCount { get; private set; }

    internal long IncomingObjectPushCompletedCount { get; private set; }

    internal long IncomingObjectPushDisconnectedCount { get; private set; }

    // Convenience for complete native packets that fit in their initial
    // kind-six transfer.
    internal IReadOnlyList<byte[]> ObserveOutbound(ReadOnlySpan<byte> packet)
        => ObserveOutbound(InitialTransferKind, packet);

    internal IReadOnlyList<byte[]> ObserveOutbound(
        byte transferKind,
        ReadOnlySpan<byte> fragment)
    {
        if (transferKind == InitialTransferKind)
        {
            return ObserveInitialFragment(fragment);
        }

        if (transferKind == ContinuationTransferKind)
        {
            return ObserveContinuationFragment(fragment);
        }

        ResetOutboundPacket();
        return [];
    }

    List<byte[]> ObserveInitialFragment(ReadOnlySpan<byte> fragment)
    {
        ResetOutboundPacket();
        if (fragment.Length < 4)
        {
            return [];
        }

        int packetLength = BinaryPrimitives.ReadUInt16LittleEndian(fragment) + 4;
        if (fragment.Length > packetLength)
        {
            return [];
        }
        if (fragment.Length == packetLength)
        {
            return ObserveCompletePacket(fragment);
        }

        _outboundPacket = new byte[packetLength];
        fragment.CopyTo(_outboundPacket);
        _outboundPacketLength = fragment.Length;
        return [];
    }

    List<byte[]> ObserveContinuationFragment(ReadOnlySpan<byte> fragment)
    {
        if (_outboundPacket is null ||
            fragment.Length == 0 ||
            fragment.Length > _outboundPacket.Length - _outboundPacketLength)
        {
            ResetOutboundPacket();
            return [];
        }

        fragment.CopyTo(_outboundPacket.AsSpan(_outboundPacketLength));
        _outboundPacketLength += fragment.Length;
        if (_outboundPacketLength != _outboundPacket.Length)
        {
            return [];
        }

        byte[] packet = _outboundPacket;
        ResetOutboundPacket();
        return ObserveCompletePacket(packet);
    }

    void ResetOutboundPacket()
    {
        _outboundPacket = null;
        _outboundPacketLength = 0;
    }

    internal static IReadOnlyList<(byte Kind, byte[] Payload)>
        FragmentInbound(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
        {
            return [];
        }

        var fragments = new List<(byte Kind, byte[] Payload)>();
        byte kind = InitialTransferKind;
        while (!packet.IsEmpty)
        {
            int length = Math.Min(
                MaximumTransferPayloadLength,
                packet.Length);
            fragments.Add((kind, packet[..length].ToArray()));
            packet = packet[length..];
            kind = ContinuationTransferKind;
        }
        return fragments;
    }

    List<byte[]> ObserveCompletePacket(ReadOnlySpan<byte> packet)
    {
        if (!TryReadL2capPacket(packet, out ushort channelId, out ReadOnlySpan<byte> payload))
        {
            return [];
        }

        if (channelId == PeerSdpChannelId && _handsetSdpChannelId != 0)
        {
            byte[]? response = ObserveSdp(payload);
            return response is null ? [] : [response];
        }
        if (channelId == PeerRfcommChannelId && _handsetRfcommChannelId != 0)
        {
            return ObserveRfcomm(payload);
        }
        return channelId == SignalingChannelId
            ? ObserveSignaling(payload)
            : [];
    }

    static bool TryReadL2capPacket(
        ReadOnlySpan<byte> packet,
        out ushort channelId,
        out ReadOnlySpan<byte> payload)
    {
        channelId = 0;
        payload = default;
        if (packet.Length < 4 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet) != packet.Length - 4)
        {
            return false;
        }

        channelId = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        payload = packet[4..];
        return true;
    }

    List<byte[]> ObserveSignaling(ReadOnlySpan<byte> commands)
    {
        var responses = new List<byte[]>();
        while (commands.Length >= 4)
        {
            byte code = commands[0];
            byte identifier = commands[1];
            int commandLength =
                BinaryPrimitives.ReadUInt16LittleEndian(commands[2..]);
            if (commandLength > commands.Length - 4)
            {
                break;
            }

            ReadOnlySpan<byte> command = commands.Slice(4, commandLength);
            ObserveSignalingCommand(code, identifier, command, responses);
            commands = commands[(4 + commandLength)..];
        }
        return responses;
    }

    void ObserveSignalingCommand(
        byte code,
        byte identifier,
        ReadOnlySpan<byte> command,
        List<byte[]> responses)
    {
        switch (code)
        {
            case 0x02 when command.Length == 4:
                ObserveConnectionRequest(identifier, command, responses);
                break;
            case 0x04 when command.Length >= 4:
                ObserveConfigurationRequest(identifier, command, responses);
                break;
            case 0x06 when command.Length == 4:
                ObserveDisconnectionRequest(identifier, command, responses);
                break;
        }
    }

    void ObserveConnectionRequest(
        byte identifier,
        ReadOnlySpan<byte> command,
        List<byte[]> responses)
    {
        ushort psm = BinaryPrimitives.ReadUInt16LittleEndian(command);
        ushort handsetSourceChannel =
            BinaryPrimitives.ReadUInt16LittleEndian(command[2..]);
        if (handsetSourceChannel < 0x0040 || psm is not (SdpPsm or RfcommPsm))
        {
            return;
        }

        ushort peerChannelId = OpenPeerChannel(psm, handsetSourceChannel);
        responses.Add(CreateConnectionResponse(
            identifier,
            handsetSourceChannel,
            peerChannelId));
        responses.Add(CreateConfigurationRequest(
            _nextPeerIdentifier++,
            handsetSourceChannel));
    }

    ushort OpenPeerChannel(ushort psm, ushort handsetSourceChannel)
    {
        if (psm == SdpPsm)
        {
            _handsetSdpChannelId = handsetSourceChannel;
            return PeerSdpChannelId;
        }

        _handsetRfcommChannelId = handsetSourceChannel;
        return PeerRfcommChannelId;
    }

    void ObserveConfigurationRequest(
        byte identifier,
        ReadOnlySpan<byte> command,
        List<byte[]> responses)
    {
        ushort peerDestinationChannel =
            BinaryPrimitives.ReadUInt16LittleEndian(command);
        ushort handsetSourceChannel = peerDestinationChannel switch
        {
            PeerSdpChannelId => _handsetSdpChannelId,
            PeerRfcommChannelId => _handsetRfcommChannelId,
            _ => 0,
        };
        if (handsetSourceChannel == 0)
        {
            return;
        }

        responses.Add(CreateConfigurationResponse(
            identifier,
            handsetSourceChannel,
            command[4..]));
    }

    void ObserveDisconnectionRequest(
        byte identifier,
        ReadOnlySpan<byte> command,
        List<byte[]> responses)
    {
        ushort peerDestinationChannel =
            BinaryPrimitives.ReadUInt16LittleEndian(command);
        ushort handsetSourceChannel =
            BinaryPrimitives.ReadUInt16LittleEndian(command[2..]);
        bool isSdp = peerDestinationChannel == PeerSdpChannelId &&
            handsetSourceChannel == _handsetSdpChannelId;
        bool isRfcomm = peerDestinationChannel == PeerRfcommChannelId &&
            handsetSourceChannel == _handsetRfcommChannelId;
        if (!isSdp && !isRfcomm)
        {
            return;
        }

        responses.Add(CreateDisconnectionResponse(
            identifier,
            peerDestinationChannel,
            handsetSourceChannel));
        ClosePeerChannel(isSdp);
    }

    void ClosePeerChannel(bool isSdp)
    {
        if (isSdp)
        {
            _handsetSdpChannelId = 0;
            return;
        }

        ResetRfcomm();
    }

    List<byte[]> ObserveRfcomm(ReadOnlySpan<byte> packet)
    {
        var responses = new List<byte[]>();
        while (!packet.IsEmpty)
        {
            if (!TryReadRfcommFrame(packet, out BluetoothRfcommFrame frame))
            {
                break;
            }

            ObserveRfcommFrame(frame, responses);
            packet = packet[frame.Length..];
        }
        return responses;
    }

    void ObserveRfcommFrame(
        BluetoothRfcommFrame frame,
        List<byte[]> responses)
    {
        switch (frame.FrameType)
        {
            case 0x2f when frame.PollFinal:
                ObserveRfcommSabm(frame.Dlci, responses);
                break;
            case 0x43 when frame.PollFinal:
                ObserveRfcommDisc(frame.Dlci, responses);
                break;
            case 0x63 when frame.Dlci == LocalObjectPushDlci:
                ObserveRfcommUa();
                break;
            case 0xef:
                ObserveRfcommUih(frame, responses);
                break;
        }
    }

    void ObserveRfcommSabm(byte dlci, List<byte[]> responses)
    {
        OpenRfcommChannel(dlci);
        responses.Add(WrapRfcommForHandset(
            CreateRfcommFrame(dlci, control: 0x73, [])));
    }

    void OpenRfcommChannel(byte dlci)
    {
        if (dlci == 0)
        {
            RfcommMultiplexerEstablishedCount += _rfcommMultiplexerOpen ? 0 : 1;
            _rfcommMultiplexerOpen = true;
            return;
        }

        if (_rfcommMultiplexerOpen && dlci == ObjectPushRfcommChannel * 2)
        {
            RfcommObjectPushLinkEstablishedCount += _rfcommObjectPushLinkOpen ? 0 : 1;
            _rfcommObjectPushLinkOpen = true;
        }
    }

    void ObserveRfcommDisc(byte dlci, List<byte[]> responses)
    {
        responses.Add(WrapRfcommForHandset(
            CreateRfcommFrame(dlci, control: 0x73, [])));
        CloseRfcommChannel(dlci);
    }

    void CloseRfcommChannel(byte dlci)
    {
        if (dlci == 0)
        {
            _rfcommMultiplexerOpen = false;
            _rfcommObjectPushLinkOpen = false;
            return;
        }

        if (dlci == ObjectPushRfcommChannel * 2)
        {
            _rfcommObjectPushLinkOpen = false;
        }
    }

    void ObserveRfcommUa()
    {
        switch (_incomingObjectPushPhase)
        {
            case BluetoothL2capControllerIncomingObjectPushPhase.AwaitUa:
                _incomingObjectPushPhase =
                    BluetoothL2capControllerIncomingObjectPushPhase.AwaitMsc;
                break;
            case BluetoothL2capControllerIncomingObjectPushPhase.AwaitDiscUa:
                IncomingObjectPushDisconnectedCount++;
                _incomingObjectPushPhase =
                    BluetoothL2capControllerIncomingObjectPushPhase.Complete;
                break;
        }
    }

    void ObserveRfcommUih(
        BluetoothRfcommFrame frame,
        List<byte[]> responses)
    {
        if (frame.Dlci == 0 && _rfcommMultiplexerOpen)
        {
            ObserveMultiplexerInformation(frame.Information, responses);
            return;
        }
        if (frame.Dlci == ObjectPushRfcommChannel * 2 && _rfcommObjectPushLinkOpen)
        {
            ObserveObjectPushInformation(frame, responses);
            return;
        }
        if (frame.Dlci == LocalObjectPushDlci)
        {
            ObserveIncomingObexResponse(frame.Information, responses);
        }
    }

    void ObserveMultiplexerInformation(
        ReadOnlySpan<byte> information,
        List<byte[]> responses)
    {
        byte[]? response = ObserveRfcommMultiplexerCommand(information);
        if (response is not null)
        {
            responses.Add(WrapRfcommForHandset(
                CreateRfcommFrame(dlci: 0, control: 0xef, response)));
        }

        ObserveIncomingMultiplexerPhase(information, responses);
    }

    void ObserveIncomingMultiplexerPhase(
        ReadOnlySpan<byte> information,
        List<byte[]> responses)
    {
        if (_incomingObjectPushPhase ==
                BluetoothL2capControllerIncomingObjectPushPhase.AwaitPnResponse &&
            IsIncomingObjectPushPnResponse(information))
        {
            responses.Add(CreateIncomingObjectPushSabm());
            _incomingObjectPushPhase =
                BluetoothL2capControllerIncomingObjectPushPhase.AwaitUa;
            return;
        }
        if (_incomingObjectPushPhase ==
                BluetoothL2capControllerIncomingObjectPushPhase.AwaitMsc &&
            IsIncomingObjectPushMsc(information))
        {
            // Queue CONNECT after acknowledging the native E3 MSC.
            responses.Add(CreateIncomingObexConnect());
            _incomingObjectPushPhase =
                BluetoothL2capControllerIncomingObjectPushPhase.AwaitConnectResponse;
        }
    }

    void ObserveObjectPushInformation(
        BluetoothRfcommFrame frame,
        List<byte[]> responses)
    {
        long finalPutCount = ObexFinalPutRequestCount;
        foreach (byte[] response in ObserveObex(frame.Information))
        {
            // One returned credit replaces the consumed frame.
            responses.Add(WrapRfcommForHandset(
                CreateRfcommFrame(
                    frame.Dlci,
                    control: 0xff,
                    response,
                    credit: 1)));
        }

        StartIncomingObjectPushIfReady(finalPutCount, responses);
    }

    void StartIncomingObjectPushIfReady(
        long previousFinalPutCount,
        List<byte[]> responses)
    {
        if (_incomingObjectPushPhase !=
                BluetoothL2capControllerIncomingObjectPushPhase.AwaitOutboundFinalPut ||
            ObexFinalPutRequestCount <= previousFinalPutCount)
        {
            return;
        }

        // RFCOMM has one PN transaction in this firmware. Queue the final PUT
        // response first, then negotiate the peer's local server channel.
        responses.Add(CreateIncomingObjectPushPn());
        IncomingObjectPushStartedCount++;
        _incomingObjectPushPhase =
            BluetoothL2capControllerIncomingObjectPushPhase.AwaitPnResponse;
    }

    void ObserveIncomingObexResponse(
        ReadOnlySpan<byte> information,
        List<byte[]> responses)
    {
        switch (_incomingObjectPushPhase)
        {
            case BluetoothL2capControllerIncomingObjectPushPhase.AwaitConnectResponse:
                ObserveIncomingObexConnectResponse(information, responses);
                break;
            case BluetoothL2capControllerIncomingObjectPushPhase.AwaitPutResponse:
                ObserveIncomingObexPutResponse(information, responses);
                break;
            case BluetoothL2capControllerIncomingObjectPushPhase.AwaitDisconnectResponse:
                ObserveIncomingObexDisconnectResponse(information, responses);
                break;
        }
    }

    void ObserveIncomingObexConnectResponse(
        ReadOnlySpan<byte> information,
        List<byte[]> responses)
    {
        if (!information.SequenceEqual(ObexConnectSuccessResponse))
        {
            return;
        }
        _incomingObjectOffset = 0;
        responses.Add(CreateNextIncomingObexPut());
        _incomingObjectPushPhase =
            BluetoothL2capControllerIncomingObjectPushPhase.AwaitPutResponse;
    }

    void ObserveIncomingObexPutResponse(
        ReadOnlySpan<byte> information,
        List<byte[]> responses)
    {
        bool continues = information.SequenceEqual(ObexContinueResponse) &&
            _incomingObjectOffset < _incomingObject.Data.Length;
        bool completes = information.SequenceEqual(ObexSuccessResponse) &&
            _incomingObjectOffset == _incomingObject.Data.Length;
        switch (continues, completes)
        {
            case (true, _):
                responses.Add(CreateNextIncomingObexPut());
                break;
            case (_, true):
                IncomingObjectPushCompletedCount++;
                responses.Add(CreateIncomingObexDisconnect());
                _incomingObjectPushPhase =
                    BluetoothL2capControllerIncomingObjectPushPhase.AwaitDisconnectResponse;
                break;
        }
    }

    void ObserveIncomingObexDisconnectResponse(
        ReadOnlySpan<byte> information,
        List<byte[]> responses)
    {
        if (!information.SequenceEqual(ObexSuccessResponse))
        {
            return;
        }
        responses.Add(CreateIncomingObjectPushDisc());
        _incomingObjectPushPhase =
            BluetoothL2capControllerIncomingObjectPushPhase.AwaitDiscUa;
    }

    static bool IsIncomingObjectPushPnResponse(
        ReadOnlySpan<byte> information) =>
        information.Length == 10 &&
        information[0] == 0x81 &&
        information[1] == 0x11 &&
        (information[2] & 0x3f) == LocalObjectPushDlci;

    static bool IsIncomingObjectPushMsc(
        ReadOnlySpan<byte> information) =>
        information.SequenceEqual(
            new byte[]
            {
                0xe3, 0x05,
                (byte)((LocalObjectPushDlci << 2) | 0x03),
                0x0d,
            });

    byte[] CreateIncomingObjectPushPn()
    {
        ReadOnlySpan<byte> parameterNegotiation =
        [
            0x83, 0x11,
            LocalObjectPushDlci,
            0xf0,
            0x17,
            0x00,
            0xf6, 0x00,
            0x00,
            0x07,
        ];
        return WrapRfcommForHandset(
            CreateRfcommFrame(
                dlci: 0,
                control: 0xef,
                parameterNegotiation));
    }

    byte[] CreateIncomingObjectPushSabm() =>
        WrapRfcommForHandset(
            CreateAddressedRfcommFrame(
                control: 0x3f,
                [],
                (byte)((LocalObjectPushDlci << 2) | 0x01)));

    byte[] CreateIncomingObexConnect()
    {
        ReadOnlySpan<byte> obexConnect =
        [
            0x80, 0x00, 0x07,
            0x10, 0x00, 0x02, 0x00,
        ];
        return WrapRfcommForHandset(
            CreateRfcommFrame(
                LocalObjectPushDlci,
                control: 0xef,
                obexConnect));
    }

    byte[] CreateIncomingObexDisconnect()
    {
        ReadOnlySpan<byte> obexDisconnect = [0x81, 0x00, 0x03];
        return WrapRfcommForHandset(
            CreateRfcommFrame(
                LocalObjectPushDlci,
                control: 0xef,
                obexDisconnect));
    }

    byte[] CreateIncomingObjectPushDisc() =>
        WrapRfcommForHandset(
            CreateAddressedRfcommFrame(
                control: 0x53,
                [],
                (byte)((LocalObjectPushDlci << 2) | 0x01)));

    byte[] CreateNextIncomingObexPut()
    {
        byte[] nameHeader = _incomingObjectOffset == 0
            ? CreateUnicodeHeader(0x01, _incomingObject.Name)
            : [];
        byte[] typeHeader = _incomingObjectOffset == 0
            ? CreateByteSequenceHeader(
                0x42,
                Encoding.ASCII.GetBytes(_incomingObject.MediaType + "\0"))
            : [];
        int availableBodyLength =
            MaximumIncomingObexPacketLength -
            3 -
            nameHeader.Length -
            typeHeader.Length -
            3;
        if (availableBodyLength < 0)
        {
            throw new InvalidOperationException(
                "The staged Bluetooth object's name and media type are too long.");
        }
        int bodyLength = Math.Min(
            availableBodyLength,
            _incomingObject.Data.Length - _incomingObjectOffset);
        bool final =
            _incomingObjectOffset + bodyLength == _incomingObject.Data.Length;
        byte[] obexPut = new byte[
            3 + nameHeader.Length + typeHeader.Length + 3 + bodyLength];
        obexPut[0] = final ? (byte)0x82 : (byte)0x02;
        BinaryPrimitives.WriteUInt16BigEndian(
            obexPut.AsSpan(1),
            checked((ushort)obexPut.Length));
        nameHeader.CopyTo(obexPut.AsSpan(3));
        typeHeader.CopyTo(obexPut.AsSpan(3 + nameHeader.Length));
        int bodyHeaderOffset = 3 + nameHeader.Length + typeHeader.Length;
        obexPut[bodyHeaderOffset] = final ? (byte)0x49 : (byte)0x48;
        BinaryPrimitives.WriteUInt16BigEndian(
            obexPut.AsSpan(bodyHeaderOffset + 1),
            checked((ushort)(bodyLength + 3)));
        _incomingObject.Data.Span.Slice(_incomingObjectOffset, bodyLength)
            .CopyTo(obexPut.AsSpan(bodyHeaderOffset + 3));
        _incomingObjectOffset += bodyLength;

        return WrapRfcommForHandset(
            CreateRfcommFrame(
                LocalObjectPushDlci,
                control: 0xef,
                obexPut));
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

    static byte[] CreateByteSequenceHeader(byte id, ReadOnlySpan<byte> value)
    {
        var header = new byte[3 + value.Length];
        header[0] = id;
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(1),
            checked((ushort)header.Length));
        value.CopyTo(header.AsSpan(3));
        return header;
    }

    static MiaTransferObject CreateDefaultIncomingObject() => new(
        "I.vcf",
        "TEXT/X-VCARD",
        "BEGIN:VCARD\r\nVERSION:2.1\r\nN:;I\r\n"u8.ToArray()
            .Concat("TEL;CELL:456\r\nEND:VCARD\r\n"u8.ToArray())
            .ToArray());

    static byte[]? ObserveRfcommMultiplexerCommand(
        ReadOnlySpan<byte> information)
    {
        // Parameter Negotiation. The native command constructor at 010c4592
        // proposes its receive MTU and initial credit count. Accept both
        // values and select credit-based flow in the response.
        if (information.Length == 10 &&
            information[0] == 0x83 &&
            information[1] == 0x11)
        {
            byte[] response = information.ToArray();
            response[0] = 0x81;
            response[3] = 0xe0;
            return response;
        }

        // Modem Status Command. 010c57f8 emits E3 05 <DLCI|3> 0D and the
        // response gate accepts the same payload with its C/R bit cleared.
        if (information.Length == 4 &&
            information[0] == 0xe3 &&
            information[1] == 0x05)
        {
            byte[] response = information.ToArray();
            response[0] = 0xe1;
            return response;
        }

        return null;
    }

    List<byte[]> ObserveObex(ReadOnlySpan<byte> information)
    {
        _obexReceiveBuffer.AddRange(information.ToArray());
        var responses = new List<byte[]>();
        while (_obexReceiveBuffer.Count >= 3)
        {
            int packetLength =
                (_obexReceiveBuffer[1] << 8) |
                _obexReceiveBuffer[2];
            if (packetLength < 3)
            {
                _obexReceiveBuffer.Clear();
                break;
            }
            if (packetLength > _obexReceiveBuffer.Count)
            {
                break;
            }

            byte[] packet =
                _obexReceiveBuffer.GetRange(0, packetLength).ToArray();
            _obexReceiveBuffer.RemoveRange(0, packetLength);
            byte[]? response = ObserveCompleteObexPacket(packet);
            if (response is not null)
            {
                responses.Add(response);
            }
        }
        return responses;
    }

    internal byte[]? ObserveCompleteObexPacket(ReadOnlySpan<byte> packet)
        => packet[0] switch
        {
            0x80 when packet.Length >= 7 => ObserveObexConnect(),
            0x02 or 0x82 => ObserveObexPut(packet),
            0x81 => ObserveObexDisconnect(),
            0xff => ObexSuccessResponse.ToArray(),
            _ => null,
        };

    byte[] ObserveObexConnect()
    {
        ObexConnectRequestCount++;
        _receivedObject.Clear();
        _receivedObjectName = "received.bin";
        _receivedObjectMediaType = "application/octet-stream";
        _receivedObjectExpectedLength = null;
        _hasReceivedObject = false;
        return ObexConnectSuccessResponse.ToArray();
    }

    byte[] ObserveObexPut(ReadOnlySpan<byte> packet)
    {
        ObexPutRequestCount++;
        if (!ObserveObexPutHeaders(packet[3..]))
        {
            return ObexContinueResponse.ToArray();
        }
        return CompleteObexPut();
    }

    byte[] CompleteObexPut()
    {
        if (_receivedObjectExpectedLength is int expectedLength &&
            expectedLength != _receivedObject.Count)
        {
            return [0xc0, 0x00, 0x03];
        }
        ObexFinalPutRequestCount++;
        _hasReceivedObject = true;
        return ObexSuccessResponse.ToArray();
    }

    byte[] ObserveObexDisconnect()
    {
        ObexDisconnectRequestCount++;
        return ObexSuccessResponse.ToArray();
    }

    bool ObserveObexPutHeaders(ReadOnlySpan<byte> headers)
    {
        bool endOfBody = false;
        while (!headers.IsEmpty)
        {
            if (!TryObserveObexHeader(headers, ref endOfBody, out int consumedLength))
            {
                return endOfBody;
            }
            headers = headers[consumedLength..];
        }
        return endOfBody;
    }

    bool TryObserveObexHeader(
        ReadOnlySpan<byte> headers,
        ref bool endOfBody,
        out int consumedLength) => (headers[0] & 0xc0) switch
        {
            0x00 or 0x40 => TryObserveLengthPrefixedObexHeader(
                headers,
                ref endOfBody,
                out consumedLength),
            0x80 => TryObserveOneByteObexHeader(headers, out consumedLength),
            _ => TryObserveFourByteObexHeader(headers, out consumedLength),
        };

    bool TryObserveLengthPrefixedObexHeader(
        ReadOnlySpan<byte> headers,
        ref bool endOfBody,
        out int consumedLength)
    {
        if (!TryReadLengthPrefixedObexHeader(
                headers,
                out byte headerId,
                out ReadOnlySpan<byte> value,
                out consumedLength))
        {
            return false;
        }

        switch (headerId)
        {
            case 0x48 or 0x49:
                return ObserveObexBodyHeader(headerId, value, ref endOfBody);
            case 0x01:
                ObserveObexNameHeader(value);
                break;
            case 0x42:
                ObserveObexTypeHeader(value);
                break;
        }
        return true;
    }

    static bool TryReadLengthPrefixedObexHeader(
        ReadOnlySpan<byte> headers,
        out byte headerId,
        out ReadOnlySpan<byte> value,
        out int consumedLength)
    {
        headerId = 0;
        value = default;
        consumedLength = 0;
        if (headers.Length < 3)
        {
            return false;
        }

        int headerLength = BinaryPrimitives.ReadUInt16BigEndian(headers[1..]);
        if (headerLength < 3 || headerLength > headers.Length)
        {
            return false;
        }

        headerId = headers[0];
        value = headers.Slice(3, headerLength - 3);
        consumedLength = headerLength;
        return true;
    }

    bool ObserveObexBodyHeader(
        byte headerId,
        ReadOnlySpan<byte> value,
        ref bool endOfBody)
    {
        endOfBody |= headerId == 0x49;
        _receivedObject.AddRange(value.ToArray());
        if (_receivedObject.Count <= MaximumReceivedObjectLength)
        {
            return true;
        }

        _receivedObject.Clear();
        endOfBody = false;
        return false;
    }

    void ObserveObexNameHeader(ReadOnlySpan<byte> value)
    {
        if (value.Length >= 2 && value[^2] == 0 && value[^1] == 0)
        {
            value = value[..^2];
        }
        if (value.Length % 2 != 0)
        {
            return;
        }

        _receivedObjectName = Encoding.BigEndianUnicode.GetString(value);
        _receivedObjectMediaType = GuessMediaType(_receivedObjectName);
    }

    void ObserveObexTypeHeader(ReadOnlySpan<byte> value)
    {
        int terminator = value.IndexOf((byte)0);
        if (terminator >= 0)
        {
            value = value[..terminator];
        }
        _receivedObjectMediaType = Encoding.ASCII.GetString(value);
    }

    static bool TryObserveOneByteObexHeader(
        ReadOnlySpan<byte> headers,
        out int consumedLength)
    {
        consumedLength = 2;
        return headers.Length >= consumedLength;
    }

    bool TryObserveFourByteObexHeader(
        ReadOnlySpan<byte> headers,
        out int consumedLength)
    {
        consumedLength = 5;
        if (headers.Length < consumedLength)
        {
            return false;
        }
        if (headers[0] == 0xc3)
        {
            uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(headers[1..]);
            _receivedObjectExpectedLength = declaredLength <= MaximumReceivedObjectLength
                ? (int)declaredLength
                : -1;
        }
        return true;
    }

    static string GuessMediaType(string name) =>
        Path.GetExtension(name).ToUpperInvariant() switch
        {
            ".VCF" => "TEXT/X-VCARD",
            ".VCS" => "TEXT/X-VCALENDAR",
            ".TXT" => "text/plain",
            ".GIF" => "image/gif",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".PNG" => "image/png",
            ".EMY" => "audio/x-emelody",
            ".IMY" => "audio/i-melody",
            ".MID" or ".MIDI" => "audio/midi",
            _ => "application/octet-stream",
        };

    static bool TryReadRfcommFrame(
        ReadOnlySpan<byte> packet,
        out BluetoothRfcommFrame frame)
    {
        frame = default;
        if (!TryReadRfcommHeader(
                packet,
                out byte dlci,
                out byte control,
                out BluetoothRfcommFrameLayout layout))
        {
            return false;
        }

        int frameLength = layout.InformationOffset + layout.InformationLength + 1;
        if (!HasValidRfcommFcs(packet, frameLength, layout.FcsHeaderLength))
        {
            return false;
        }

        frame = new BluetoothRfcommFrame(
            frameLength,
            dlci,
            control,
            packet.Slice(layout.InformationOffset, layout.InformationLength));
        return true;
    }

    static bool TryReadRfcommHeader(
        ReadOnlySpan<byte> packet,
        out byte dlci,
        out byte control,
        out BluetoothRfcommFrameLayout layout)
    {
        dlci = 0;
        control = 0;
        layout = default;
        if (packet.Length < 4)
        {
            return false;
        }

        dlci = (byte)(packet[0] >> 2);
        control = packet[1];
        return TryReadRfcommFrameLayout(packet, control, out layout);
    }

    static bool HasValidRfcommFcs(
        ReadOnlySpan<byte> packet,
        int frameLength,
        int fcsHeaderLength) =>
        frameLength <= packet.Length &&
        packet[frameLength - 1] == CalculateRfcommFcs(packet[..fcsHeaderLength]);

    static bool TryReadRfcommFrameLayout(
        ReadOnlySpan<byte> packet,
        byte control,
        out BluetoothRfcommFrameLayout layout)
    {
        int informationLength = packet[2] >> 1;
        int informationOffset = 3;
        int protectedHeaderLength = 3;
        bool usesTwoByteLength = (packet[2] & 1) == 0;
        if (usesTwoByteLength && packet.Length < 5)
        {
            layout = default;
            return false;
        }
        if (usesTwoByteLength)
        {
            informationLength |= packet[3] << 7;
            informationOffset++;
            protectedHeaderLength++;
        }

        bool isUih = (control & 0xef) == 0xef;
        if (isUih && (control & 0x10) != 0)
        {
            informationOffset++;
        }
        layout = new BluetoothRfcommFrameLayout(
            informationOffset,
            informationLength,
            isUih ? 2 : protectedHeaderLength);
        return true;
    }

    byte[] WrapRfcommForHandset(ReadOnlySpan<byte> frame)
    {
        var packet = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            _handsetRfcommChannelId);
        frame.CopyTo(packet.AsSpan(4));
        return packet;
    }

    static byte[] CreateRfcommFrame(
        byte dlci,
        byte control,
        ReadOnlySpan<byte> information,
        byte? credit = null) =>
        CreateAddressedRfcommFrame(
            control,
            information,
            (byte)((dlci << 2) | 0x03),
            credit);

    static byte[] CreateAddressedRfcommFrame(
        byte control,
        ReadOnlySpan<byte> information,
        byte address,
        byte? credit = null)
    {
        bool isUih = (control & 0xef) == 0xef;
        int lengthFieldLength = isUih ? 2 : 1;
        int creditLength = credit.HasValue ? 1 : 0;
        var frame = new byte[
            2 + lengthFieldLength + creditLength +
            information.Length + 1];
        frame[0] = address;
        frame[1] = control;
        if (isUih)
        {
            frame[2] = (byte)((information.Length << 1) & 0xfe);
            frame[3] = (byte)(information.Length >> 7);
        }
        else
        {
            frame[2] = checked((byte)((information.Length << 1) | 1));
        }

        int cursor = 2 + lengthFieldLength;
        if (credit.HasValue)
        {
            frame[cursor++] = credit.Value;
        }
        information.CopyTo(frame.AsSpan(cursor));
        int fcsHeaderLength = isUih ? 2 : 3;
        frame[^1] = CalculateRfcommFcs(frame.AsSpan(0, fcsHeaderLength));
        return frame;
    }

    static byte CalculateRfcommFcs(ReadOnlySpan<byte> bytes)
    {
        byte fcs = 0xff;
        foreach (byte next in bytes)
        {
            byte tableIndex = (byte)(fcs ^ next);
            for (int bit = 0; bit < 8; bit++)
            {
                tableIndex = (tableIndex & 1) != 0
                    ? (byte)((tableIndex >> 1) ^ 0xe0)
                    : (byte)(tableIndex >> 1);
            }
            fcs = tableIndex;
        }
        return (byte)(0xff - fcs);
    }

    void ResetRfcomm()
    {
        _handsetRfcommChannelId = 0;
        _rfcommMultiplexerOpen = false;
        _rfcommObjectPushLinkOpen = false;
        _obexReceiveBuffer.Clear();
        _incomingObjectPushPhase = _initiateIncomingObjectPush
            ? BluetoothL2capControllerIncomingObjectPushPhase.AwaitOutboundFinalPut
            : BluetoothL2capControllerIncomingObjectPushPhase.Disabled;
    }

    byte[]? ObserveSdp(ReadOnlySpan<byte> pdu)
    {
        const byte serviceSearchAttributeRequest = 0x06;
        const int requestParameterLength = 0x13;
        ReadOnlySpan<byte> expectedParameters =
        [
            // ServiceSearchPattern: OBEX Object Push (UUID 0x1105).
            0x35, 0x03, 0x19, 0x11, 0x05,
            // MaximumAttributeByteCount.
            0xff, 0xff,
            // AttributeIDList: ServiceClassIDList,
            // ProtocolDescriptorList, and SupportedFormatsList.
            0x35, 0x09,
            0x09, 0x00, 0x01,
            0x09, 0x00, 0x04,
            0x09, 0x03, 0x03,
            // ContinuationState: none.
            0x00,
        ];
        if (_handsetSdpChannelId == 0 ||
            pdu.Length != 5 + requestParameterLength ||
            pdu[0] != serviceSearchAttributeRequest ||
            BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]) !=
                requestParameterLength ||
            !pdu[5..].SequenceEqual(expectedParameters))
        {
            return null;
        }

        ushort transactionId =
            BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        SdpServiceSearchAttributeRequestCount++;
        return CreateObjectPushServiceSearchAttributeResponse(transactionId);
    }

    byte[] CreateObjectPushServiceSearchAttributeResponse(
        ushort transactionId)
    {
        ReadOnlySpan<byte> attributeLists =
        [
            // One service record containing the three attributes requested by
            // the native handset. All multi-byte SDP fields are big-endian.
            0x35, 0x29,
              0x35, 0x27,
                // 0x0001 ServiceClassIDList: OBEX Object Push.
                0x09, 0x00, 0x01,
                0x35, 0x03, 0x19, 0x11, 0x05,
                // 0x0004 ProtocolDescriptorList:
                // L2CAP, RFCOMM channel 9, then OBEX.
                0x09, 0x00, 0x04,
                0x35, 0x11,
                  0x35, 0x03, 0x19, 0x01, 0x00,
                  0x35, 0x05, 0x19, 0x00, 0x03,
                    0x08, ObjectPushRfcommChannel,
                  0x35, 0x03, 0x19, 0x00, 0x08,
                // 0x0303 SupportedFormatsList: vCard 2.1 and any object.
                0x09, 0x03, 0x03,
                0x35, 0x04, 0x08, 0x01, 0x08, 0xff,
        ];
        int parameterLength =
            sizeof(ushort) + attributeLists.Length + sizeof(byte);
        int sdpLength = 5 + parameterLength;
        var packet = new byte[4 + sdpLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)sdpLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            _handsetSdpChannelId);
        packet[4] = 0x07;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(5),
            transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(7),
            checked((ushort)parameterLength));
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(9),
            checked((ushort)attributeLists.Length));
        attributeLists.CopyTo(packet.AsSpan(11));
        // ContinuationState=none remains zero.
        return packet;
    }

    static byte[] CreateConnectionResponse(
        byte identifier,
        ushort handsetSourceChannel,
        ushort peerChannelId)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            SignalingChannelId);
        packet[4] = 0x03;
        packet[5] = identifier;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            peerChannelId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10),
            handsetSourceChannel);
        // Result=success and status=no-further-information remain zero.
        return packet;
    }

    static byte[] CreateDisconnectionResponse(
        byte identifier,
        ushort peerDestinationChannel,
        ushort handsetSourceChannel)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            SignalingChannelId);
        packet[4] = 0x07;
        packet[5] = identifier;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            peerDestinationChannel);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10),
            handsetSourceChannel);
        return packet;
    }

    static byte[] CreateConfigurationRequest(
        byte identifier,
        ushort handsetDestinationChannel)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            SignalingChannelId);
        packet[4] = 0x04;
        packet[5] = identifier;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            handsetDestinationChannel);
        // Flags=continuation-not-present remains zero.
        return packet;
    }

    static byte[] CreateConfigurationResponse(
        byte identifier,
        ushort handsetSourceChannel,
        ReadOnlySpan<byte> options)
    {
        var packet = new byte[14 + options.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)(10 + options.Length)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            SignalingChannelId);
        packet[4] = 0x05;
        packet[5] = identifier;
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(6),
            checked((ushort)(6 + options.Length)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            handsetSourceChannel);
        // Flags=continuation-not-present and Result=success remain zero.
        options.CopyTo(packet.AsSpan(14));
        return packet;
    }
}
