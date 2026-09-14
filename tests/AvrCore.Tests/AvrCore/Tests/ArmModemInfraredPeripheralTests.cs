// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Arm7Core;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemInfraredPeripheralTests
{
    const int SharedSerialControlAddress = 0x00800104;
    const int InfraredTransmitDataAddress = 0x00800b20;
    const int InfraredReceiveDataAddress = 0x00800b30;
    const int InfraredReceiveCountAddress = 0x00800b3c;
    const byte BeginningOfFrame = 0xc0;
    const byte EndOfFrame = 0xc1;
    const byte Escape = 0x7d;

    [Fact]
    public void UnterminatedOversizeFrameIsDiscardedAndTheNextFrameCanStart()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        bus.WriteByte(InfraredTransmitDataAddress, BeginningOfFrame, ArmAccess.None);
        for (int i = 0; i < 100000; i++)
            bus.WriteByte(InfraredTransmitDataAddress, 0, ArmAccess.None);
        Assert.Equal(1, infrared.InvalidTransmittedFrameCount);
        Assert.Equal(0, infrared.TransmittedFrameCount);
        infrared.QueueReceivedFrame([0xff, 0xbf]);
        while (bus.ReadByte(InfraredReceiveCountAddress, ArmAccess.None) != 0)
            bus.WriteByte(InfraredTransmitDataAddress,
                (byte)bus.ReadByte(InfraredReceiveDataAddress, ArmAccess.None), ArmAccess.None);
        Assert.Equal(1, infrared.TransmittedFrameCount);
    }

    [Fact]
    public void CompleteFramesCrossTheSirBoundaryWithEscapingAndFcs()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        byte[] frame =
            [0xff, 0xbf, BeginningOfFrame, EndOfFrame, Escape, 0x00];
        var transmitted = new List<byte>();
        infrared.FrameTransmitted += value => transmitted.AddRange(value.Span);

        infrared.QueueReceivedFrame(frame);

        var wire = new List<byte>();
        while (bus.ReadByte(InfraredReceiveCountAddress, ArmAccess.None) != 0)
        {
            wire.Add((byte)bus.ReadByte(
                InfraredReceiveDataAddress,
                ArmAccess.None));
        }
        Assert.Equal(BeginningOfFrame, wire[0]);
        Assert.Equal(EndOfFrame, wire[^1]);
        Assert.Contains(Escape, wire);

        foreach (byte value in wire)
        {
            bus.WriteByte(InfraredTransmitDataAddress, value, ArmAccess.None);
        }

        Assert.Equal(frame, transmitted);
        Assert.Equal(1, infrared.ReceivedFrameCount);
        Assert.Equal(1, infrared.TransmittedFrameCount);
        Assert.Equal(0, infrared.InvalidTransmittedFrameCount);
    }

    [Fact]
    public void NativeUartConfigurationPublishesPortState()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);

        bus.WriteByte(SharedSerialControlAddress, 4, ArmAccess.None);
        Assert.True(infrared.PortEnabled);

        bus.WriteByte(SharedSerialControlAddress, 0x0c, ArmAccess.None);
        Assert.False(infrared.PortEnabled);

        bus.WriteByte(SharedSerialControlAddress, 0, ArmAccess.None);
        Assert.False(infrared.PortEnabled);
    }

    [Fact]
    public void ObjectPeerPushesACompleteObexObjectThroughNativeLayers()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        using var peer = new ArmModemInfraredObjectPeer(infrared);
        peer.StageObjectForHandset(new(
            "I.vcf",
            "TEXT/X-VCARD",
            "IRDA"u8.ToArray()));

        AssertHandsetObjectPushConnection(bus, peer);
        AssertHandsetObjectPushTransfer(bus, peer);
        MiaInfraredTransferStatus status = peer.CaptureStatus();
        Assert.Equal(MiaInfraredTransferPhase.Completed, status.Phase);
        Assert.Equal(1, status.ObjectsSentToHandset);
    }

    static void AssertHandsetObjectPushConnection(
        ArmModemBus bus,
        ArmModemInfraredObjectPeer peer)
    {
        bus.WriteByte(SharedSerialControlAddress, 4, ArmAccess.None);
        bus.IdleUntilCycle(bus.Cycles + 250_000);
        Assert.Equal(0x3f, DrainQueuedFrame(bus)[1]);

        peer.ObserveFrame(
        [
            0xfe, 0xbf, 0x01,
            0x93, 0x32, 0xde, 0x37,
            0x44, 0x33, 0x22, 0x11,
            0x00, 0x00, 0x00,
            0x91, 0x24, 0x00, (byte)'T', (byte)'6', (byte)'8',
        ]);
        Assert.Equal(0xff, DrainQueuedFrame(bus)[12]);
        bus.IdleUntilCycle(bus.Cycles + 250_000);
        Assert.Equal(0x93, DrainQueuedFrame(bus)[1]);

        peer.ObserveFrame(
        [
            0x22, 0x73,
            0x93, 0x32, 0xde, 0x37,
            0x44, 0x33, 0x22, 0x11,
        ]);
        Assert.Equal(
            new byte[] { 0x80, 0x10, 0x01, 0x00 },
            DrainQueuedFrame(bus)[2..]);

        peer.ObserveFrame([0x22, 0x30, 0x90, 0x00, 0x81, 0x00]);
        byte[] iasQuery = DrainQueuedFrame(bus);
        Assert.Equal(0x84, iasQuery[4]);
        Assert.Contains(
            "IrDA:TinyTP:LsapSel",
            System.Text.Encoding.ASCII.GetString(iasQuery),
            StringComparison.Ordinal);

        peer.ObserveFrame(
        [
            0x22, 0x52, 0x10, 0x00,
            0x84, 0x00, 0x00, 0x01, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x00, 0x04,
        ]);
        Assert.Equal(
            new byte[] { 0x80, 0x10, 0x02, 0x01 },
            DrainQueuedFrame(bus)[2..]);

        peer.ObserveFrame([0x22, 0x71]);
        Assert.Equal(
            new byte[] { 0x84, 0x12, 0x01, 0x00, 0x10 },
            DrainQueuedFrame(bus)[2..]);
        peer.ObserveFrame([0x22, 0x91]);
        bus.IdleUntilCycle(bus.Cycles + 250_000);
        Assert.Equal(new byte[] { 0x23, 0x51 }, DrainQueuedFrame(bus));

        peer.ObserveFrame(
            [0x22, 0x94, 0x92, 0x04, 0x81, 0x00, 0x19]);
        Assert.Equal(
            new byte[] { 0x04, 0x12, 0x01, 0x80, 0x00, 0x07,
                0x10, 0x00, 0x02, 0x00 },
            DrainQueuedFrame(bus)[2..]);

        peer.ObserveFrame([0x22, 0xb6, 0x80, 0x01, 0x01, 0x00]);
        Assert.Equal(
            new byte[] { 0x81, 0x00, 0x81, 0x00 },
            DrainQueuedFrame(bus)[2..]);
    }

    static void AssertHandsetObjectPushTransfer(
        ArmModemBus bus,
        ArmModemInfraredObjectPeer peer)
    {
        peer.ObserveFrame(
            [0x22, 0xd8, 0x12, 0x04, 0x01,
             0xa0, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00]);

        peer.ObserveFrame(
        [
            0x22, 0xfa, 0x00, 0x01, 0x84, 0x06,
            (byte)'D', (byte)'e', (byte)'v', (byte)'i', (byte)'c', (byte)'e',
            0x0a,
            (byte)'D', (byte)'e', (byte)'v', (byte)'i', (byte)'c',
            (byte)'e', (byte)'N', (byte)'a', (byte)'m', (byte)'e',
        ]);
        byte[] deviceName = DrainQueuedFrame(bus);
        Assert.Contains("Codex IrDA", System.Text.Encoding.ASCII.GetString(
            deviceName), StringComparison.Ordinal);

        peer.ObserveFrame([0x22, 0x1c, 0x80, 0x01, 0x02, 0x01]);
        byte[] put = DrainQueuedFrame(bus);
        Assert.Equal(0x82, put[5]);
        Assert.Contains((byte)0x49, put);
        Assert.Equal("IRDA"u8.ToArray(), put[^4..]);

        peer.ObserveFrame(
            [0x22, 0x3e, 0x12, 0x04, 0x01, 0xa0, 0x00, 0x03]);
        byte[] disconnect = DrainQueuedFrame(bus);
        Assert.Equal(0x81, disconnect[5]);
        peer.ObserveFrame(
            [0x22, 0x50, 0x12, 0x04, 0x01, 0xa0, 0x00, 0x03]);
        DrainQueuedFrame(bus);
    }

    [Fact]
    public void ObjectPeerReceivesAndCapturesAHandsetObject()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        using var peer = new ArmModemInfraredObjectPeer(infrared);

        AssertHandsetObjectPushHandshake(bus, peer);
        AssertReceivedHandsetObject(peer);
    }

    static void AssertHandsetObjectPushHandshake(
        ArmModemBus bus,
        ArmModemInfraredObjectPeer peer)
    {
        peer.ObserveFrame(
        [
            0xff, 0x3f, 0x01,
            0x93, 0x32, 0xde, 0x37,
            0xff, 0xff, 0xff, 0xff,
            0x00, 0x00, 0x00,
        ]);
        byte[] discovery = DrainQueuedFrame(bus);
        Assert.Equal(0xfe, discovery[0]);
        Assert.Contains("Codex", System.Text.Encoding.ASCII.GetString(discovery), StringComparison.Ordinal);

        peer.ObserveFrame(
        [
            0xff, 0x93,
            0x93, 0x32, 0xde, 0x37,
            0x44, 0x33, 0x22, 0x11,
            0x22,
        ]);
        Assert.Equal(0x73, DrainQueuedFrame(bus)[1]);

        peer.ObserveFrame([0x23, 0x10, 0x80, 0x01, 0x01, 0x00]);
        Assert.Equal(
            new byte[] { 0x81, 0x00, 0x81, 0x00 },
            DrainQueuedFrame(bus)[2..]);

        byte[] iasClass = "OBEX"u8.ToArray();
        byte[] iasAttribute = "IrDA:TinyTP:LsapSel"u8.ToArray();
        peer.ObserveFrame(
        [
            0x23, 0x32, 0x00, 0x01, 0x84,
            (byte)iasClass.Length, .. iasClass,
            (byte)iasAttribute.Length, .. iasAttribute,
        ]);
        byte[] iasResponse = DrainQueuedFrame(bus);
        Assert.Equal(0x04, iasResponse[^1]);

        peer.ObserveFrame([0x23, 0x54, 0x80, 0x01, 0x02, 0x01]);
        Assert.Equal(new byte[] { 0x22, 0x71 }, DrainQueuedFrame(bus));
        peer.ObserveFrame([0x23, 0x56, 0x84, 0x05, 0x01, 0x00, 0x10]);
        Assert.Equal(
            new byte[] { 0x85, 0x04, 0x81, 0x00, 0x19 },
            DrainQueuedFrame(bus)[2..]);

        peer.ObserveFrame(
            [0x23, 0x78, 0x04, 0x05, 0x01,
             0x80, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00]);
        Assert.Equal(
            new byte[] { 0x05, 0x04, 0x01, 0xa0, 0x00, 0x07,
                0x10, 0x00, 0x02, 0x00 },
            DrainQueuedFrame(bus)[2..]);

        peer.ObserveFrame(
        [
            0x23, 0x9a, 0x04, 0x05, 0x01,
            0x82, 0x00, 0x29,
            0x01, 0x00, 0x0f,
            0x00, (byte)'I', 0x00, (byte)'.', 0x00, (byte)'v',
            0x00, (byte)'c', 0x00, (byte)'f', 0x00, 0x00,
            0x42, 0x00, 0x10,
            (byte)'T', (byte)'E', (byte)'X', (byte)'T',
            (byte)'/', (byte)'X', (byte)'-', (byte)'V',
            (byte)'C', (byte)'A', (byte)'R', (byte)'D', 0x00,
            0x49, 0x00, 0x07,
            (byte)'I', (byte)'R', (byte)'D', (byte)'A',
        ]);
        byte[] success = DrainQueuedFrame(bus);
        Assert.Equal(0xa0, success[5]);
    }

    static void AssertReceivedHandsetObject(ArmModemInfraredObjectPeer peer)
    {
        MiaTransferObject received = Assert.IsType<MiaTransferObject>(
            peer.GetReceivedObject());
        Assert.Equal("I.vcf", received.Name);
        Assert.Equal("TEXT/X-VCARD", received.MediaType);
        Assert.Equal("IRDA"u8.ToArray(), received.Data.ToArray());
        Assert.Equal(1, peer.ObjectsReceivedFromHandset);
    }

    [Fact]
    public void ObjectPeerReassemblesAndCreditsALargeSegmentedGif()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        using var peer = new ArmModemInfraredObjectPeer(infrared);

        peer.ObserveFrame(
        [
            0xff, 0x93,
            0x93, 0x32, 0xde, 0x37,
            0x44, 0x33, 0x22, 0x11,
            0x22,
        ]);
        DrainQueuedFrame(bus);

        byte nativeSequence = 0;
        void SendNativeInformation(ReadOnlySpan<byte> information)
        {
            byte control = (byte)(
                0x10 | ((nativeSequence & 0x07) << 1));
            nativeSequence++;
            peer.ObserveFrame([0x23, control, .. information]);
        }

        SendNativeInformation([0x84, 0x05, 0x01, 0x00, 0x10]);
        Assert.Equal(
            new byte[] { 0x85, 0x04, 0x81, 0x00, 0x19 },
            DrainQueuedFrame(bus)[2..]);

        SendNativeInformation(
            [0x04, 0x05, 0x01,
             0x80, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00]);
        Assert.Equal(
            new byte[] { 0x05, 0x04, 0x01, 0xa0, 0x00, 0x07,
                0x10, 0x00, 0x02, 0x00 },
            DrainQueuedFrame(bus)[2..]);

        byte[] gif = Enumerable.Range(0, 34_474)
            .Select(index => (byte)(index % 251))
            .ToArray();
        "GIF89a"u8.CopyTo(gif);
        gif[^1] = 0x3b;
        byte[] put = CreateFinalObexPutPacket("frame.gif", "image/gif", gif);

        const int segmentLength = 180;
        for (int offset = 0; offset < put.Length; offset += segmentLength)
        {
            int count = Math.Min(segmentLength, put.Length - offset);
            var information = new byte[3 + count];
            information[0] = 0x04;
            information[1] = 0x05;
            information[2] = offset == 0 ? (byte)0x01 : (byte)0x00;
            put.AsSpan(offset, count).CopyTo(information.AsSpan(3));
            SendNativeInformation(information);

            byte[] response = DrainQueuedFrame(bus)[2..];
            Assert.Equal(
                offset + count == put.Length
                    ? new byte[] { 0x05, 0x04, 0x01, 0xa0, 0x00, 0x03 }
                    : new byte[] { 0x05, 0x04, 0x01 },
                response);
        }

        MiaTransferObject received = Assert.IsType<MiaTransferObject>(
            peer.GetReceivedObject());
        Assert.Equal("frame.gif", received.Name);
        Assert.Equal("image/gif", received.MediaType);
        Assert.Equal(gif, received.Data.ToArray());
        Assert.Equal(
            gif.Length,
            peer.CaptureStatus().ObjectBytesTransferred);
    }

    static byte[] CreateFinalObexPutPacket(
        string name,
        string mediaType,
        byte[] payload)
    {
        byte[] encodedName = Encoding.BigEndianUnicode.GetBytes(name + '\0');
        byte[] encodedType = Encoding.ASCII.GetBytes(mediaType + '\0');
        int packetLength =
            3 +
            3 + encodedName.Length +
            3 + encodedType.Length +
            5 +
            3 + payload.Length;
        var packet = new byte[packetLength];
        packet[0] = 0x82;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(1),
            checked((ushort)packetLength));
        int headerOffset = 3;
        packet[headerOffset] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerOffset + 1),
            checked((ushort)(3 + encodedName.Length)));
        encodedName.CopyTo(packet.AsSpan(headerOffset + 3));
        headerOffset += 3 + encodedName.Length;
        packet[headerOffset] = 0x42;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerOffset + 1),
            checked((ushort)(3 + encodedType.Length)));
        encodedType.CopyTo(packet.AsSpan(headerOffset + 3));
        headerOffset += 3 + encodedType.Length;
        packet[headerOffset] = 0xc3;
        BinaryPrimitives.WriteInt32BigEndian(
            packet.AsSpan(headerOffset + 1),
            payload.Length);
        headerOffset += 5;
        packet[headerOffset] = 0x49;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerOffset + 1),
            checked((ushort)(3 + payload.Length)));
        payload.CopyTo(packet.AsSpan(headerOffset + 3));
        return packet;
    }

    [Fact]
    public void ObjectPeerPreservesHashesAcrossSequentialMultiPutTransactions()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        using var peer = new ArmModemInfraredObjectPeer(infrared);

        peer.ObserveFrame(
        [
            0xff, 0x93,
            0x93, 0x32, 0xde, 0x37,
            0x44, 0x33, 0x22, 0x11,
            0x22,
        ]);
        DrainQueuedFrame(bus);

        byte nativeSequence = 0;
        void SendNativeInformation(ReadOnlySpan<byte> information)
        {
            byte control = (byte)(0x10 | ((nativeSequence & 0x07) << 1));
            nativeSequence++;
            peer.ObserveFrame([0x23, control, .. information]);
        }

        void Transfer(string name, byte[] payload)
        {
            SendNativeInformation([0x84, 0x05, 0x01, 0x00, 0x10]);
            Assert.Equal(
                new byte[] { 0x85, 0x04, 0x81, 0x00, 0x19 },
                DrainQueuedFrame(bus)[2..]);
            SendNativeInformation(
                [0x04, 0x05, 0x01,
                 0x80, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00]);
            Assert.Equal(0xa0, DrainQueuedFrame(bus)[5]);

            List<byte[]> puts =
                CreateFinalOpcodeMultiPutTransaction(name, payload);
            Assert.True(puts.Count >= 3);
            for (var index = 0; index < puts.Count; index++)
            {
                SendNativeInformation(
                    [0x04, 0x05, 0x01, .. puts[index]]);
                byte[] response = DrainQueuedFrame(bus)[2..];
                Assert.Equal(
                    index == puts.Count - 1 ? (byte)0xa0 : (byte)0x90,
                    response[3]);
            }

            MiaTransferObject received = Assert.IsType<MiaTransferObject>(
                peer.GetReceivedObject());
            Assert.Equal(name, received.Name);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(payload)),
                Convert.ToHexString(SHA256.HashData(received.Data.Span)));

            SendNativeInformation(
                [0x04, 0x05, 0x01, 0x81, 0x00, 0x03]);
            Assert.Equal(0xa0, DrainQueuedFrame(bus)[5]);
        }

        byte[] first = Enumerable.Range(0, 357)
            .Select(index => (byte)(index * 17 + 3))
            .ToArray();
        byte[] second = Enumerable.Range(0, 613)
            .Select(index => (byte)(index * 29 + 11))
            .ToArray();
        Transfer("first.gif", first);
        Transfer("second.bin", second);

        Assert.Equal(2, peer.ObjectsReceivedFromHandset);
        Assert.Equal(second.Length, peer.CaptureStatus().ObjectBytesTransferred);
    }

    [Fact]
    public void ProgressStatusDoesNotCopyTheStagedPayload()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var infrared = new ArmModemInfraredPeripheral(bus);
        using var peer = new ArmModemInfraredObjectPeer(infrared);
        peer.StageObjectForHandset(new(
            "large.bin",
            "application/octet-stream",
            new byte[1_048_576]));

        _ = peer.CaptureStatus();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var observedBytes = 0;
        for (var index = 0; index < 32; index++)
        {
            observedBytes += peer.CaptureStatus()
                .StagedObject
                .GetValueOrDefault()
                .Data
                .Length;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(32 * 1_048_576, observedBytes);
        Assert.True(
            allocated < 16_384,
            $"Progress snapshots allocated {allocated:n0} bytes.");
    }

    static List<byte[]> CreateFinalOpcodeMultiPutTransaction(
        string name,
        byte[] payload)
    {
        const int firstBodyLength = 30;
        const int laterBodyLength = 160;
        var packets = new List<byte[]>();
        int offset = 0;
        while (offset < payload.Length)
        {
            bool first = offset == 0;
            int bodyLength = Math.Min(
                first ? firstBodyLength : laterBodyLength,
                payload.Length - offset);
            bool endOfBody = offset + bodyLength == payload.Length;
            byte[] nameHeader = first
                ? CreateObexSequenceHeader(
                    0x01,
                    Encoding.BigEndianUnicode.GetBytes(name + "\0"))
                : [];
            byte[] typeHeader = first
                ? CreateObexSequenceHeader(0x42, "image/gif\0"u8.ToArray())
                : [];
            byte[] lengthHeader = first
                ?
                [
                    0xc3,
                    (byte)(payload.Length >> 24),
                    (byte)(payload.Length >> 16),
                    (byte)(payload.Length >> 8),
                    (byte)payload.Length,
                ]
                : [];
            var packet = new byte[
                3 + nameHeader.Length + typeHeader.Length +
                lengthHeader.Length + 3 + bodyLength];
            // The handset uses the final opcode for each request. End-of-Body,
            // not this bit, determines whether the object itself is complete.
            packet[0] = 0x82;
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(1),
                checked((ushort)packet.Length));
            int headerOffset = 3;
            nameHeader.CopyTo(packet.AsSpan(headerOffset));
            headerOffset += nameHeader.Length;
            typeHeader.CopyTo(packet.AsSpan(headerOffset));
            headerOffset += typeHeader.Length;
            lengthHeader.CopyTo(packet.AsSpan(headerOffset));
            headerOffset += lengthHeader.Length;
            packet[headerOffset] = endOfBody ? (byte)0x49 : (byte)0x48;
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(headerOffset + 1),
                checked((ushort)(bodyLength + 3)));
            payload.AsSpan(offset, bodyLength)
                .CopyTo(packet.AsSpan(headerOffset + 3));
            packets.Add(packet);
            offset += bodyLength;
        }
        return packets;
    }

    static byte[] CreateObexSequenceHeader(byte id, ReadOnlySpan<byte> value)
    {
        var header = new byte[3 + value.Length];
        header[0] = id;
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(1),
            checked((ushort)header.Length));
        value.CopyTo(header.AsSpan(3));
        return header;
    }

    static byte[] DrainQueuedFrame(ArmModemBus bus)
    {
        var state = new InfraredFrameDecodeState();
        while (bus.ReadByte(InfraredReceiveCountAddress, ArmAccess.None) != 0)
        {
            byte value = (byte)bus.ReadByte(
                InfraredReceiveDataAddress,
                ArmAccess.None);
            byte[]? frame = ConsumeInfraredByte(state, value);
            if (frame is not null)
            {
                return frame;
            }
        }

        throw new Xunit.Sdk.XunitException("No queued SIR frame was available.");
    }

    static byte[]? ConsumeInfraredByte(
        InfraredFrameDecodeState state,
        byte value)
    {
        switch (value)
        {
            case BeginningOfFrame:
                state.Decoded.Clear();
                state.Receiving = true;
                state.Escaped = false;
                return null;
            case EndOfFrame when state.Receiving:
                Assert.True(state.Decoded.Count >= 4);
                return state.Decoded[..^2].ToArray();
            case var _ when state.Escaped:
                state.Decoded.Add((byte)(value ^ 0x20));
                state.Escaped = false;
                return null;
            case Escape:
                state.Escaped = true;
                return null;
            default:
                if (state.Receiving)
                {
                    state.Decoded.Add(value);
                }
                return null;
        }
    }
}
