// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Arm7Core;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemBluetoothPeripheralTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void EmptyDataFramesDoNotAcknowledgeTheSameTransferTwice(byte kind)
    {
        var modem = new ArmModem(Convert.FromHexString("00000001040000000000A0E1"),
            ArmModemFlashProfile.StM36Dr216C);
        var bus = modem.Bus;
        using var peripheral = new ArmModemBluetoothPeripheral(modem)
        {
            EmitIdleTransferFrames = false,
        };
        bus.WriteByte(0x00800800, 0x17, ArmAccess.None);
        for (int frame = 0; frame < 10000; frame++)
        {
            bus.WriteByte(0x00800804, 0x10, ArmAccess.None);
            bus.WriteByte(0x00800804, 1, ArmAccess.None);
            bus.WriteByte(0x00800804, 3, ArmAccess.None);
            bus.WriteByte(0x0080080c, 0, ArmAccess.None);
            bus.WriteByte(0x00800810, kind, ArmAccess.None);
            bus.WriteByte(0x0080080c, 3, ArmAccess.None);
            bus.IdleUntilCycle(bus.Cycles + 60000);
            bus.WriteByte(0x00800800, 0, ArmAccess.None);
        }
        for (int frame = 0; frame < 10; frame++)
        {
            bus.IdleUntilCycle(bus.Cycles + 60000);
            bus.WriteByte(0x00800800, 0, ArmAccess.None);
        }
        Assert.Equal(10000, peripheral.FixedTransferCompletionCount);
        bus.IdleUntilCycle(bus.Cycles + 60000);
        Assert.Equal(0, bus.DspInterruptStatus);
    }

    [Fact]
    public void LegacyAuthenticationMatchesTheNativePairingTrace()
    {
        Span<byte> response = stackalloc byte[4];
        Span<byte> authenticatedCipheringOffset = stackalloc byte[12];

        BluetoothLegacyCrypto.Authenticate(
            Convert.FromHexString("1CC0E4A7C2BD629760380178E3BE392D"),
            Convert.FromHexString("9E653F32D7812860937B6403A2F1EC39"),
            Convert.FromHexString("112233445566"),
            new BluetoothLegacyAuthenticationOutput(
                response,
                authenticatedCipheringOffset));

        Assert.Equal(
            Convert.FromHexString("F1541243"),
            response.ToArray());
    }

    [Fact]
    public void LegacyPairingDerivesTheNativeCombinationLinkKey()
    {
        byte[] initializationRandom =
            Convert.FromHexString("7FF0E259F34F1B426DFC1D0FDB5FA779");
        byte[] encryptedCombinationRandom =
            Convert.FromHexString("A4EE4EF9F6B06FF789EA04E3D5F417EF");
        byte[] peerAddress = Convert.FromHexString("112233445566");
        byte[] handsetAddress = Convert.FromHexString("00D20CEC0100");
        byte[] pinAndPeerAddress =
            [.. "0000"u8, .. peerAddress];
        Span<byte> initializationKey = stackalloc byte[16];

        BluetoothLegacyCrypto.DeriveInitializationKey(
            initializationRandom,
            pinAndPeerAddress,
            initializationKey);

        Span<byte> combinationRandom = stackalloc byte[16];
        for (var index = 0; index < combinationRandom.Length; index++)
        {
            combinationRandom[index] =
                (byte)(encryptedCombinationRandom[index] ^
                    initializationKey[index]);
        }
        Assert.Equal(
            Convert.FromHexString("4261865BFF625B531A7EC0149AE79669"),
            combinationRandom.ToArray());

        Span<byte> handsetKeyPart = stackalloc byte[16];
        Span<byte> peerKeyPart = stackalloc byte[16];
        BluetoothLegacyCrypto.DeriveCombinationKeyPart(
            combinationRandom,
            handsetAddress,
            handsetKeyPart);
        BluetoothLegacyCrypto.DeriveCombinationKeyPart(
            combinationRandom,
            peerAddress,
            peerKeyPart);
        for (var index = 0; index < handsetKeyPart.Length; index++)
        {
            handsetKeyPart[index] ^= peerKeyPart[index];
        }

        Assert.Equal(
            Convert.FromHexString("1CC0E4A7C2BD629760380178E3BE392D"),
            handsetKeyPart.ToArray());
    }

    [Fact]
    public void LegacyControllerFollowsTheRecoveredKindSevenExchange()
    {
        var controller = new BluetoothLegacyPairingController(
            Convert.FromHexString("112233445566"),
            Convert.FromHexString("00D20CEC0100"),
            "0000"u8);

        AssertRecoveredPairingSetup(controller);
        AssertRecoveredPairingCompletion(controller);
        Assert.True(controller.IsComplete);
        Assert.False(controller.AuthenticationFailed);
        Assert.Equal(
            Convert.FromHexString("1CC0E4A7C2BD629760380178E3BE392D"),
            controller.LinkKey.ToArray());
    }

    static void AssertRecoveredPairingSetup(
        BluetoothLegacyPairingController controller)
    {
        AssertRecord(
            "5004EA3100000000000000000000000000",
            controller.Begin());
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString(
                "4E04EA310000000000")));
        AssertSingleRecord(
            "1600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString(
                "107FF0E259F34F1B426DFC1D0FDB5FA779")));
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("090B06")));
        AssertSingleRecord(
            "12A4EE4EF9F6B06FF789EA04E3D5F417EF",
            controller.ObserveOutbound(Convert.FromHexString(
                "12A4EE4EF9F6B06FF789EA04E3D5F417EF")));
    }

    static void AssertRecoveredPairingCompletion(
        BluetoothLegacyPairingController controller)
    {
        IReadOnlyList<byte[]> authenticationRecords =
            controller.ObserveOutbound(Convert.FromHexString(
                "169E653F32D7812860937B6403A2F1EC39"));
        Assert.Collection(
            authenticationRecords,
            record => AssertRecord(
                "18F1541243000000000000000000000000",
                record),
            record => AssertRecord(
                "1600000000000000000000000000000000",
                record));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("18394634E2")));
        AssertSingleRecord(
            "1E00000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("1E01")));
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("090F23")));
        AssertSingleRecord(
            "2005000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("2005")));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("0610")));
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString(
                "2263E0EB7286BED85B857EC472A0884E42")));
        AssertSingleRecord(
            "6200000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("62")));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("6E800C")));
        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString("0A")),
            record => AssertRecord(
                "6600000000000000000000000000000000",
                record),
            record => AssertRecord(
                "6E800C0000000000000000000000000000",
                record),
            record => AssertRecord(
                "0A00000000000000000000000000000000",
                record));
        AssertSingleRecord(
            "0D0DD00000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("0D0DD0")));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("0E13")));
    }

    [Fact]
    public void BondedConnectionAuthenticatesWithTheExistingLinkKey()
    {
        var controller = new BluetoothLegacyConnectionController(
            Convert.FromHexString("112233445566"),
            Convert.FromHexString("00D20CEC0100"),
            Convert.FromHexString("1CC0E4A7C2BD629760380178E3BE392D"));

        AssertSingleRecord(
            "5004EA3100000000000000000000000000",
            controller.Begin());
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString(
                "4E04EA310000000000")));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("66")));
        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString("62")),
            record => AssertRecord(
                "1600000000000000000000000000000000",
                record),
            record => AssertRecord(
                "6200000000000000000000000000000000",
                record));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("6E800C")));
        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString("0A")),
            record => AssertRecord(
                "6600000000000000000000000000000000",
                record),
            record => AssertRecord(
                "6E800C0000000000000000000000000000",
                record),
            record => AssertRecord(
                "0A00000000000000000000000000000000",
                record));
        AssertSingleRecord(
            "1E00000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString(
                "19394634E2")));
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("070F")));
        AssertSingleRecord(
            "2105000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("2105")));
        Assert.Empty(controller.ObserveOutbound(
            Convert.FromHexString("0810")));
        AssertSingleRecord(
            "0600000000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString(
                "2363E0EB7286BED85B857EC472A0884E42")));
        AssertSingleRecord(
            "0D0DD00000000000000000000000000000",
            controller.ObserveOutbound(Convert.FromHexString("0D0DD0")));
        controller.ObserveDataChannelEstablished();

        Assert.True(controller.IsComplete);
        Assert.False(controller.AuthenticationFailed);
    }

    [Fact]
    public void L2capAcceptsTheNativeSdpConnectionRequest()
    {
        var controller = new BluetoothL2capController();

        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString(
                "080001000201040001004000")),
            response => AssertRecord(
                "0C000100030108004100400000000000",
                response),
            configurationRequest => AssertRecord(
                "080001000401040040000000",
                configurationRequest));
        AssertSingleRecord(
            "0E00010005020A004000000000000102FB00",
            controller.ObserveOutbound(Convert.FromHexString(
                "0C00010004020800410000000102FB00")));
    }

    [Fact]
    public void L2capReassemblesTheNativeSdpFragmentsAndAdvertisesObjectPush()
    {
        var controller = new BluetoothL2capController();
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000201040001004000"));

        Assert.Empty(controller.ObserveOutbound(
            BluetoothL2capController.InitialTransferKind,
            Convert.FromHexString(
                "1800410006000000133503191105FFFF35")));
        AssertSingleRecord(
            "33004000070000002E002B352935270900013503191105" +
            "09000435113503190100350519000308093503190008" +
            "0903033504080108FF00",
            controller.ObserveOutbound(
                BluetoothL2capController.ContinuationTransferKind,
                Convert.FromHexString(
                    "0909000109000409030300")));

        Assert.Equal(1, controller.SdpServiceSearchAttributeRequestCount);
    }

    [Fact]
    public void L2capResponsesUseTheRecoveredKindSixThenKindFiveFraming()
    {
        IReadOnlyList<(byte Kind, byte[] Payload)> fragments =
            BluetoothL2capController.FragmentInbound(
                Enumerable.Range(0, 38).Select(value => (byte)value).ToArray());

        Assert.Collection(
            fragments,
            fragment =>
            {
                Assert.Equal(
                    BluetoothL2capController.InitialTransferKind,
                    fragment.Kind);
                Assert.Equal(
                    Enumerable.Range(0, 17).Select(value => (byte)value),
                    fragment.Payload);
            },
            fragment =>
            {
                Assert.Equal(
                    BluetoothL2capController.ContinuationTransferKind,
                    fragment.Kind);
                Assert.Equal(
                    Enumerable.Range(17, 17).Select(value => (byte)value),
                    fragment.Payload);
            },
            fragment =>
            {
                Assert.Equal(
                    BluetoothL2capController.ContinuationTransferKind,
                    fragment.Kind);
                Assert.Equal(
                    Enumerable.Range(34, 4).Select(value => (byte)value),
                    fragment.Payload);
            });
    }

    [Fact]
    public void L2capClosesSdpAndOpensTheNativeRfcommPsm()
    {
        var controller = new BluetoothL2capController();
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000201040001004000"));

        AssertSingleRecord(
            "080001000703040041004000",
            controller.ObserveOutbound(Convert.FromHexString(
                "080001000603040041004000")));
        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString(
                "080001000204040003004100")),
            response => AssertRecord(
                "0C000100030408004200410000000000",
                response),
            configurationRequest => AssertRecord(
                "080001000402040041000000",
                configurationRequest));
        AssertSingleRecord(
            "0E00010005050A004100000000000102FB00",
            controller.ObserveOutbound(Convert.FromHexString(
                "0C00010004050800420000000102FB00")));
    }

    [Fact]
    public void RfcommAndObexFollowTheRecoveredObjectPushExchange()
    {
        var controller = new BluetoothL2capController();
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000201040001004000"));
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000603040041004000"));
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000204040003004100"));

        AssertRfcommObjectPushConnection(controller);
        AssertObexObjectPushTransfer(controller);
        AssertObjectPushState(controller);
    }

    static void AssertRfcommObjectPushConnection(
        BluetoothL2capController controller)
    {
        // SABM/UA on DLCI zero. The FCS bytes 1c/d7 come from the table at
        // modem ROM 0119f840 used by 010c3db4.
        AssertSingleRecord(
            "04004100037301D7",
            controller.ObserveOutbound(Convert.FromHexString(
                "04004200033F011C")));

        // PN accepts the live native MTU (0x00f6) and seven-credit proposal,
        // selecting credit flow with convergence-layer response 0xe0.
        AssertSingleRecord(
            "0F00410003EF1400811112E01700F600000770",
            controller.ObserveOutbound(Convert.FromHexString(
                "0F00420003EF1400831112F01700F600000770")));

        // DLCI 18 is RFCOMM server channel 9 in the SDP record.
        AssertSingleRecord(
            "040041004B7301F9",
            controller.ObserveOutbound(Convert.FromHexString(
                "040042004B3F0132")));
        AssertSingleRecord(
            "0900410003EF0800E1054B0D70",
            controller.ObserveOutbound(Convert.FromHexString(
                "0900420003EF0800E3054B0D70")));
    }

    static void AssertObexObjectPushTransfer(
        BluetoothL2capController controller)
    {
        AssertSingleRecord(
            "0D0041004BFF0E0001A0000710000200D2",
            controller.ObserveOutbound(Convert.FromHexString(
                "0D0042004BFF0E000380000710000200D2")));
        AssertSingleRecord(
            "090041004BFF060001A00003D2",
            controller.ObserveOutbound(Convert.FromHexString(
                "540042004BFF9C000182004E01000F0041" +
                "002E007600630066000049003C42454749" +
                "4E3A56434152440D0A56455253494F4E3A" +
                "322E310D0A4E3A3B410D0A54454C3B4345" +
                "4C4C3A3132330D0A454E443A5643415244" +
                "0D0AD2")));
        AssertSingleRecord(
            "090041004BFF060001A00003D2",
            controller.ObserveOutbound(Convert.FromHexString(
                "090042004BFF060001810003D2")));
        AssertSingleRecord(
            "040041004B7301F9",
            controller.ObserveOutbound(Convert.FromHexString(
                "040042004B5301D3")));
        AssertSingleRecord(
            "04004100037301D7",
            controller.ObserveOutbound(Convert.FromHexString(
                "04004200035301FD")));
    }

    static void AssertObjectPushState(BluetoothL2capController controller)
    {
        Assert.Equal(1, controller.RfcommMultiplexerEstablishedCount);
        Assert.Equal(1, controller.RfcommObjectPushLinkEstablishedCount);
        Assert.Equal(1, controller.ObexConnectRequestCount);
        Assert.Equal(1, controller.ObexPutRequestCount);
        Assert.Equal(1, controller.ObexFinalPutRequestCount);
        Assert.Equal(1, controller.ObexDisconnectRequestCount);
        Assert.Equal(
            "BEGIN:VCARD\r\n"u8.ToArray()
                .Concat("VERSION:2.1\r\n"u8.ToArray())
                .Concat("N:;A\r\n"u8.ToArray())
                .Concat("TEL;CELL:123\r\n"u8.ToArray())
                .Concat("END:VCARD\r\n"u8.ToArray()),
            controller.ObexTransferredObject.ToArray());
        MiaTransferObject received =
            Assert.IsType<MiaTransferObject>(
                controller.ObexTransferredObjectSnapshot);
        Assert.Equal("A.vcf", received.Name);
        Assert.Equal("TEXT/X-VCARD", received.MediaType);
        Assert.Equal(
            controller.ObexTransferredObject.ToArray(),
            received.Data.ToArray());
    }

    [Fact]
    public void ObexPreservesHashesAcrossSequentialMultiPutTransactions()
    {
        var controller = new BluetoothL2capController();

        void Transfer(string name, byte[] payload)
        {
            Assert.Equal(
                new byte[] { 0xa0, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00 },
                controller.ObserveCompleteObexPacket(
                    [0x80, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00]));

            List<byte[]> puts =
                CreateFinalOpcodeMultiPutTransaction(name, payload);
            Assert.True(puts.Count >= 3);
            for (var index = 0; index < puts.Count; index++)
            {
                Assert.Equal(
                    index == puts.Count - 1
                        ? new byte[] { 0xa0, 0x00, 0x03 }
                        : new byte[] { 0x90, 0x00, 0x03 },
                    controller.ObserveCompleteObexPacket(puts[index]));
            }

            MiaTransferObject received = Assert.IsType<MiaTransferObject>(
                controller.ObexTransferredObjectSnapshot);
            Assert.Equal(name, received.Name);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(payload)),
                Convert.ToHexString(SHA256.HashData(received.Data.Span)));
            Assert.Equal(
                new byte[] { 0xa0, 0x00, 0x03 },
                controller.ObserveCompleteObexPacket([0x81, 0x00, 0x03]));
        }

        byte[] first = Enumerable.Range(0, 357)
            .Select(index => (byte)(index * 13 + 5))
            .ToArray();
        byte[] second = Enumerable.Range(0, 613)
            .Select(index => (byte)(index * 31 + 7))
            .ToArray();
        Transfer("first.gif", first);
        Transfer("second.bin", second);

        Assert.Equal(2, controller.ObexConnectRequestCount);
        Assert.Equal(2, controller.ObexFinalPutRequestCount);
        Assert.Equal(2, controller.ObexDisconnectRequestCount);
        Assert.Equal(second.Length, controller.ObexTransferredObjectByteCount);
    }

    [Fact]
    public void PeerPushesVcardAfterTheNativeOutboundPutCompletes()
    {
        var controller = new BluetoothL2capController(
            initiateIncomingObjectPush: true);
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000204040003004100"));
        controller.ObserveOutbound(Convert.FromHexString(
            "04004200033F011C"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0F00420003EF1400831112F01700F600000770"));
        controller.ObserveOutbound(Convert.FromHexString(
            "040042004B3F0132"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0900420003EF0800E3054B0D70"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0D0042004BFF0E000380000710000200D2"));

        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString(
                "540042004BFF9C000182004E01000F0041" +
                "002E007600630066000049003C42454749" +
                "4E3A56434152440D0A56455253494F4E3A" +
                "322E310D0A4E3A3B410D0A54454C3B4345" +
                "4C4C3A3132330D0A454E443A5643415244" +
                "0D0AD2")),
            finalPutResponse => AssertRecord(
                "090041004BFF060001A00003D2",
                finalPutResponse),
            peerPn => AssertRecord(
                "0F00410003EF1400831115F01700F600000770",
                peerPn));

        AssertSingleRecord(
            "04004100553F015E",
            controller.ObserveOutbound(Convert.FromHexString(
                "0F00420003EF1400811115001700F600000070")));
        Assert.Empty(controller.ObserveOutbound(Convert.FromHexString(
            "0400420055730195")));
        Assert.Collection(
            controller.ObserveOutbound(Convert.FromHexString(
                "0900420003EF0800E305570D70")),
            mscResponse => AssertRecord(
                "0900410003EF0800E105570D70",
                mscResponse),
            obexConnect => AssertRecord(
                "0C00410057EF0E008000071000020044",
                obexConnect));
        AssertSingleRecord(
            "6300410057EFBC0082005E01000F004900" +
            "2E0076006300660000420010544558542F" +
            "582D56434152440049003C424547494E3A" +
            "56434152440D0A56455253494F4E3A322E" +
            "310D0A4E3A3B490D0A54454C3B43454C4C" +
            "3A3435360D0A454E443A56434152440D0A44",
            controller.ObserveOutbound(Convert.FromHexString(
                "0C00420057EF0E00A000071000020044")));
        AssertSingleRecord(
            "0800410057EF060081000344",
            controller.ObserveOutbound(Convert.FromHexString(
                "0800420057EF0600A0000344")));
        AssertSingleRecord(
            "04004100555301BF",
            controller.ObserveOutbound(Convert.FromHexString(
                "0800420057EF0600A0000344")));
        Assert.Empty(controller.ObserveOutbound(Convert.FromHexString(
            "0400420055730195")));

        Assert.Equal(1, controller.IncomingObjectPushStartedCount);
        Assert.Equal(1, controller.IncomingObjectPushCompletedCount);
        Assert.Equal(1, controller.IncomingObjectPushDisconnectedCount);
    }

    [Fact]
    public void PeerSegmentsAStagedObjectAcrossNativeObexContinueResponses()
    {
        byte[] body = Enumerable.Range(0, 400)
            .Select(value => (byte)value)
            .ToArray();
        var controller = new BluetoothL2capController(
            initiateIncomingObjectPush: true,
            incomingObject: new(
                "large.bin",
                "application/octet-stream",
                body));
        controller.ObserveOutbound(Convert.FromHexString(
            "080001000204040003004100"));
        controller.ObserveOutbound(Convert.FromHexString(
            "04004200033F011C"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0F00420003EF1400831112F01700F600000770"));
        controller.ObserveOutbound(Convert.FromHexString(
            "040042004B3F0132"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0900420003EF0800E3054B0D70"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0D0042004BFF0E000380000710000200D2"));
        controller.ObserveOutbound(Convert.FromHexString(
            "540042004BFF9C000182004E01000F0041" +
            "002E007600630066000049003C42454749" +
            "4E3A56434152440D0A56455253494F4E3A" +
            "322E310D0A4E3A3B410D0A54454C3B4345" +
            "4C4C3A3132330D0A454E443A5643415244" +
            "0D0AD2"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0F00420003EF1400811115001700F600000070"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0400420055730195"));
        controller.ObserveOutbound(Convert.FromHexString(
            "0900420003EF0800E305570D70"));

        byte[] first = Assert.Single(controller.ObserveOutbound(
            Convert.FromHexString(
                "0C00420057EF0E00A000071000020044")));
        Assert.Equal(0x02, first[8]);
        Assert.True(first.AsSpan().IndexOf(
            new byte[] { 0, (byte)'l', 0, (byte)'a' }) >= 0);

        byte[] second = Assert.Single(controller.ObserveOutbound(
            Convert.FromHexString("0800420057EF060090000344")));
        Assert.Equal(0x02, second[8]);

        byte[] final = Assert.Single(controller.ObserveOutbound(
            Convert.FromHexString("0800420057EF060090000344")));
        Assert.Equal(0x82, final[8]);
        Assert.Equal(0, controller.IncomingObjectPushCompletedCount);

        Assert.Single(controller.ObserveOutbound(
            Convert.FromHexString("0800420057EF0600A0000344")));
        Assert.Equal(1, controller.IncomingObjectPushCompletedCount);
    }

    [Fact]
    public void FixedTransferReadyPacketRaisesTheMeasuredCompletionStatus()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        bus.WriteByte(0x00800804, 0x10, ArmAccess.None);
        bus.WriteByte(0x00800804, 0x01, ArmAccess.None);
        bus.WriteByte(0x00800804, 0x05, ArmAccess.None);

        Assert.Equal(1, peripheral.FixedTransferCompletionCount);
        Assert.Equal(
            ArmModemBus.DspInterruptBit,
            bus.IrqPending & ArmModemBus.DspInterruptBit);
        Assert.Equal(0x08u, bus.ReadByte(0x00800800, ArmAccess.None));
    }

    [Fact]
    public void AnyNativePacketPortTransactionRaisesTheDspCompletionStatus()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        bus.WriteByte(0x00800804, 0x45, ArmAccess.None);
        bus.WriteByte(0x00800804, 0x02, ArmAccess.None);
        bus.WriteByte(0x00800804, 0x00, ArmAccess.None);
        bus.WriteByte(0x00800804, 0x00, ArmAccess.None);

        Assert.Equal(0x08u, bus.ReadByte(0x00800800, ArmAccess.None));
        Assert.Equal(0, peripheral.FixedTransferCompletionCount);
    }

    [Fact]
    public void NativeSecondaryTransferRaisesTheDspCompletionStatus()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        bus.WriteByte(0x0080080c, 0x00, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x0f, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x19, ArmAccess.None);
        bus.WriteByte(0x0080080c, 0x03, ArmAccess.None);

        Assert.Equal(0x08u, bus.ReadByte(0x00800800, ArmAccess.None));
        Assert.Equal(0, peripheral.FixedTransferCompletionCount);
    }

    [Fact]
    public void NativeL2capTransferRaisesTheDspCompletionStatus()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        bus.WriteByte(0x0080080c, 0x07, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x66, ArmAccess.None);
        foreach (byte value in Convert.FromHexString(
                     "080001000201040001004000"))
        {
            bus.WriteByte(0x00800810, value, ArmAccess.None);
        }
        bus.WriteByte(0x0080080c, 0x03, ArmAccess.None);

        Assert.Equal(0x08u, bus.ReadByte(
            0x00800800,
            ArmAccess.None));
        Assert.Equal(0, peripheral.FixedTransferCompletionCount);
    }

    [Fact]
    public void EmptySecondaryFrameDoesNotCreateACompletionLoop()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        bus.WriteByte(0x0080080c, 0x00, ArmAccess.None);
        bus.WriteByte(0x0080080c, 0x01, ArmAccess.None);

        Assert.Equal(0u, bus.ReadByte(
            0x00800800,
            ArmAccess.None));
        Assert.Equal(
            0u,
            bus.IrqPending & ArmModemBus.DspInterruptBit);
    }

    [Fact]
    public void CompleteControllerRecordUsesTheNativeKindThreeIngress()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        peripheral.QueueControllerRecord([0x0a, .. new byte[0x10]]);

        Assert.Equal(1, peripheral.ControllerRecordCount);
        Assert.Equal(0x10u, bus.ReadByte(0x00800800, ArmAccess.None));
        Assert.Equal(0u, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x8bu, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x0au, bus.ReadByte(0x00800810, ArmAccess.None));
    }

    [Fact]
    public void PacketPortWriteReadyIsALevelNotAnInterrupt()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        Assert.Equal(0u, bus.ReadByte(0x00800800, ArmAccess.None));
        Assert.Equal(0x20u, bus.ReadByte(0x0080000c, ArmAccess.None));
        Assert.Equal(0x20u, bus.ReadByte(0x00800c00, ArmAccess.None));
        Assert.Equal(0u, bus.IrqPending & ArmModemBus.DspInterruptBit);
    }

    [Fact]
    public void IdleTransferFramesCanBeDisabledForAnAttachedControllerSource()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus)
        {
            EmitIdleTransferFrames = false,
        };

        Assert.False(peripheral.EmitIdleTransferFrames);
    }

    [Fact]
    public void AttachedPeripheralUsesTargetedEventsInsteadOfInstructionPolling()
    {
        const uint entry = ArmModemFlash.BaseAddress;
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload,
            0xe1a00000); // nop
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            0xeafffffd); // b entry
        byte[] bih = new byte[ArmModemImage.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bih, entry);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bih.AsSpan(4),
            (uint)payload.Length);
        payload.CopyTo(bih, ArmModemImage.HeaderLength);
        var modem = new ArmModem(
            ArmModemImage.ParseBih(bih),
            ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(modem);
        ArmModemDspPacket? observed = null;
        peripheral.DspCommandQueued += packet => observed = packet;

        modem.Bus.WriteByte(
            0x014215ec,
            0x28,
            ArmAccess.None);
        modem.Bus.WriteByte(
            0x014215ed,
            0x02,
            ArmAccess.None);
        modem.Bus.WriteByte(
            0x014215ee,
            0xaa,
            ArmAccess.None);
        modem.Bus.WriteByte(
            0x014215ef,
            0x55,
            ArmAccess.None);
        modem.Bus.WriteHalf(
            0x01400c70,
            1,
            ArmAccess.None);

        Assert.False(modem.HasGeneralInstructionObservers);
        Assert.NotNull(observed);
        Assert.Equal(0x28, observed.Value.Type);
        Assert.Equal(new byte[] { 0xaa, 0x55 }, observed.Value.Payload);
        Assert.Equal(1, peripheral.DspCommandCount);
    }

    [Fact]
    public void PartialControllerRecordFailsClosed()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var peripheral = new ArmModemBluetoothPeripheral(bus);

        Assert.Throws<ArgumentException>(
            () => peripheral.QueueControllerRecord(new byte[0x10]));
        Assert.Equal(0, peripheral.ControllerRecordCount);
    }

    [Fact]
    public void InquiryAddressUsesTheNativeSevenByteBitPacking()
    {
        Span<byte> packed = stackalloc byte[7];

        ArmModemBluetoothPeripheral.EncodeInquiryAddress(
            [0x11, 0x22, 0x33, 0x44, 0x55, 0x66],
            packed);

        Assert.Equal(
            new byte[] { 0x44, 0x88, 0xcc, 0x00, 0x44, 0x55, 0x66 },
            packed.ToArray());

        byte[] decoded =
        [
            (byte)((packed[0] >> 2) | (packed[1] << 6)),
            (byte)((packed[1] >> 2) | (packed[2] << 6)),
            (byte)((packed[2] >> 2) | (packed[3] << 6)),
            packed[4],
            packed[5],
            packed[6],
        ];
        Assert.Equal(
            new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 },
            decoded);

        Span<byte> bootAddress = stackalloc byte[6];
        ArmModemBluetoothPeripheral.DecodeInquiryAddress(
            Convert.FromHexString("004833B0EC0100"),
            bootAddress);
        Assert.Equal(
            Convert.FromHexString("00D20CEC0100"),
            bootAddress.ToArray());
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

    static void AssertSingleRecord(
        string expectedHex,
        IReadOnlyList<byte[]> records)
    {
        AssertRecord(expectedHex, Assert.Single(records));
    }

    static void AssertRecord(string expectedHex, byte[] record)
    {
        Assert.Equal(Convert.FromHexString(expectedHex), record);
    }
}
