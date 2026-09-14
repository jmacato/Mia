// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class GsmCompatibilityCellTests
{
    [Fact]
    public void FchRequiresConfiguredTunedCarrierAndMinimumPower()
    {
        var strongCell = new GsmCompatibilityCell(-49, 0x0800);
        var weakCell = new GsmCompatibilityCell(-49, 0x07ff);
        var request = new AsicFchRequest(123, [CreateTransaction(-49)]);

        var strong = strongCell.Detect(request);
        var weak = weakCell.Detect(request);
        var mistuned = strongCell.Detect(new(124, [CreateTransaction(-48)]));

        Assert.True(strong.Success);
        Assert.Equal(
            GsmCompatibilityCell.AcceptedFchMetric,
            strong.Bytes.Span[2] | (strong.Bytes.Span[3] << 8));
        Assert.False(weak.Success);
        Assert.False(mistuned.Success);
        Assert.Equal(2, strongCell.FchAttemptCount);
        Assert.Equal(1, strongCell.FchSuccessCount);
    }

    [Fact]
    public void EqualizerUsesNeutralSelectorsOnlyForPresentCarrier()
    {
        var strongCell = new GsmCompatibilityCell(-49, 0x0800);
        var weakCell = new GsmCompatibilityCell(-49, 0x07ff);

        Assert.Equal(
            default,
            strongCell.Equalize(new AsicEqualizerRequest(Phase: 2)));
        Assert.Equal(
            AsicEqualizerResult.Invalid,
            weakCell.Equalize(new AsicEqualizerRequest(Phase: 2)));
    }

    [Fact]
    public void SchResultEncodesValidFirstSchFrame()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);

        var result = cell.DecodeSch(
            new byte[AsicChannelDecoder.SchInputLength]);

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x04, 0x00 }, result.Bytes.ToArray());
        Assert.Equal(1, cell.SchDecodeCount);
    }

    [Fact]
    public void ControlChannelCyclesCompleteServingCellSystemInformation()
    {
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            SwSimCard.DefaultImsi);
        var input = new byte[AsicChannelDecoder.ControlChannelInputLength];

        var si3 = cell.DecodeControlChannel(input).Bytes.ToArray();
        var si4 = cell.DecodeControlChannel(input).Bytes.ToArray();
        var si2 = cell.DecodeControlChannel(input).Bytes.ToArray();
        var si1 = cell.DecodeControlChannel(input).Bytes.ToArray();

        Assert.Equal(23, si1.Length);
        Assert.Equal(23, si2.Length);
        Assert.Equal(new byte[] { 0x55, 0x06, 0x19 }, si1[..3]);
        Assert.Equal(new byte[] { 0x59, 0x06, 0x1a }, si2[..3]);
        Assert.Equal(new byte[] { 0x49, 0x06, 0x1b }, si3[..3]);
        Assert.Equal(new byte[] { 0x31, 0x06, 0x1c }, si4[..3]);
        Assert.Equal(new byte[] { 0x00, 0xf1, 0x10, 0x00, 0x01 }, si3[5..10]);
        Assert.Equal(new byte[] { 0x00, 0xf1, 0x10, 0x00, 0x01 }, si4[3..8]);
        Assert.Equal(GsmCompatibilityCell.DefaultMsTxPowerMaxCch, si3[14]);
        Assert.Equal(GsmCompatibilityCell.DefaultMsTxPowerMaxCch, si4[8]);
        Assert.Equal(new byte[] { 0x83, 0xcf }, si1[3..5]);
        Assert.Equal(new byte[] { 0x40, 0x00, 0x00, 0x2b }, si1[19..]);
        Assert.Equal(4, cell.ControlChannelDecodeCount);
    }

    [Fact]
    public void CellAcquisitionSequenceAlwaysStartsWithSystemInformationThree()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);
        var input = new byte[AsicChannelDecoder.ControlChannelInputLength];

        var acquisitionBlocks = Enumerable.Range(0, 4)
            .Select(_ => cell.DecodeControlChannel(
                new AsicControlChannelDecodeRequest(
                    input,
                    GsmCompatibilityCell.CellAcquisitionDecoderState)))
            .ToArray();
        var firstBroadcast = cell.DecodeControlChannel(input);
        var nextAcquisition = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                input,
                GsmCompatibilityCell.CellAcquisitionDecoderState));

        Assert.Equal(
            new byte[] { 0x49, 0x06, 0x1b },
            acquisitionBlocks[0].Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x31, 0x06, 0x1c },
            acquisitionBlocks[1].Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x59, 0x06, 0x1a },
            acquisitionBlocks[2].Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x55, 0x06, 0x19 },
            acquisitionBlocks[3].Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x49, 0x06, 0x1b },
            firstBroadcast.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x49, 0x06, 0x1b },
            nextAcquisition.Bytes.Span[..3].ToArray());
    }

    [Fact]
    public void SacchSystemInformationCarriesCellIdentityAndMaximumLinkTimeout()
    {
        var locationAreaIdentity =
            GsmCompatibilityCell.EncodeLocationAreaIdentity(
                SwSimCard.DefaultImsi,
                lac: 1);

        var si5 = GsmCompatibilityCell.BuildSacchSystemInformation5();
        var si6 = GsmCompatibilityCell.BuildSacchSystemInformation6(
            locationAreaIdentity,
            bsic: 0x1f);

        Assert.Equal(23, si5.Length);
        Assert.Equal(
            new byte[] { 0x07, 0x00, 0x03, 0x03, 0x49, 0x06, 0x1d },
            si5[..7]);
        Assert.All(si5[7..], value => Assert.Equal(0x00, value));
        Assert.Equal(23, si6.Length);
        Assert.Equal(
            new byte[]
            {
                0x07, 0x00, 0x03, 0x03, 0x2d, 0x06, 0x1e,
                0x00, 0x01, 0x00, 0xf1, 0x10, 0x00, 0x01,
                0x0f, 0x08,
            },
            si6[..16]);
        Assert.All(si6[16..], value => Assert.Equal(0x2b, value));
    }

    [Fact]
    public void DecoderRejectsUnknownInputLengths()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);

        Assert.False(cell.DecodeSch(new byte[1]).Success);
        Assert.False(cell.DecodeControlChannel(new byte[1]).Success);
    }

    [Fact]
    public void ImmediateAssignmentEchoesRequestAndAssignsServingCellSdcch()
    {
        var assignment = GsmCompatibilityCell.BuildImmediateAssignment(
            requestReference: 0x5a,
            requestFrameNumber: 42431,
            bsic: 0,
            arfcn: -49);

        Assert.Equal(
            new byte[]
            {
                0x2d, 0x06, 0x3f, 0x00,
                0x41, 0x03, 0xcf,
                0x5a, 0xfe, 0x59,
                0x00, 0x00,
                0x2b, 0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
                0x2b, 0x2b, 0x2b, 0x2b, 0x2b,
            },
            assignment);
        Assert.Equal(
            assignment,
            GsmCompatibilityCell.BuildImmediateAssignment(
                0x5a,
                42431 + 42432,
                0,
                -49));
        Assert.Equal(
            GsmCompatibilityCell.BuildImmediateAssignment(0x16, 686, 0, -49),
            GsmCompatibilityCell.BuildImmediateAssignment(
                new(0x16, 0, 23, 10),
                0,
                -49));
    }

    [Fact]
    public void NativePendingReferenceAdapterReadsVerifiedParallelTable()
    {
        var cpu = new Cpu(new byte[4], 0x40000);
        cpu.Data[MiaGsmRandomAccessAdapter.PendingCountAddress] = 1;
        cpu.Data[MiaGsmRandomAccessAdapter.RequestReferenceAddress] = 0x16;
        cpu.Data[MiaGsmRandomAccessAdapter.T2Address] = 10;
        cpu.Data[MiaGsmRandomAccessAdapter.T1PrimeAddress] = 0;
        cpu.Data[MiaGsmRandomAccessAdapter.T3Address] = 23;

        Assert.Equal(
            new GsmRandomAccessReference(0x16, 0, 23, 10),
            MiaGsmRandomAccessAdapter.ReadPendingReference(cpu));

        cpu.Data[MiaGsmRandomAccessAdapter.PendingCountAddress] = 0;
        Assert.Null(MiaGsmRandomAccessAdapter.ReadPendingReference(cpu));
    }

    [Fact]
    public void NativeRachAndMatchingReferenceProduceOneAssignmentThroughControlDecoderSource()
    {
        GsmRandomAccessReference? pending = new(0x16, 0, 23, 10);
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            randomAccessReferenceSource: () => pending);
        var input = new byte[AsicChannelDecoder.ControlChannelInputLength];

        var broadcastBeforeRach = cell.DecodeControlChannel(input).Bytes.ToArray();
        cell.Encode(new AsicChannelEncoderRequest(
            new byte[] { 0x16 },
            GsmCompatibilityCell.RandomAccessUplinkEncoderState));
        var assignment = cell.DecodeControlChannel(input).Bytes.ToArray();
        var broadcast = cell.DecodeControlChannel(input).Bytes.ToArray();

        Assert.Equal(new byte[] { 0x49, 0x06, 0x1b }, broadcastBeforeRach[..3]);
        Assert.Equal(new byte[] { 0x2d, 0x06, 0x3f }, assignment[..3]);
        Assert.Equal(new byte[] { 0x16, 0x02, 0xea }, assignment[7..10]);
        Assert.Equal(new byte[] { 0x31, 0x06, 0x1c }, broadcast[..3]);
        Assert.Equal(3, cell.ControlChannelDecodeCount);
        Assert.Equal(1, cell.RandomAccessRequestCount);
        Assert.Equal(1, cell.ImmediateAssignmentCount);

        cell.Encode(new AsicChannelEncoderRequest(
            new byte[] { 0x17 },
            GsmCompatibilityCell.RandomAccessUplinkEncoderState));
        _ = cell.DecodeControlChannel(input);
        Assert.Equal(1, cell.ImmediateAssignmentCount);

        pending = null;
        _ = cell.DecodeControlChannel(input);
        pending = new(0x16, 0, 23, 10);
        cell.Encode(new AsicChannelEncoderRequest(
            new byte[] { 0x16 },
            GsmCompatibilityCell.RandomAccessUplinkEncoderState));
        _ = cell.DecodeControlChannel(input);
        Assert.Equal(2, cell.ImmediateAssignmentCount);
        Assert.Equal(3, cell.RandomAccessRequestCount);
    }

    [Fact]
    public void IncomingSmsPagesAndCompletesThroughNetworkEstablishedSapi3()
    {
        GsmRandomAccessReference? pending = null;
        var networkTime = new DateTimeOffset(
            2026,
            7,
            6,
            14,
            53,
            29,
            TimeSpan.FromHours(8));
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            randomAccessReferenceSource: () => pending,
            networkTimeProvider: () => networkTime,
            incomingPagingReadySource: () => true);
        CompleteRegistration(cell);
        cell.QueueIncomingSms("+5551234", "hello");

        var fill = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: GsmCompatibilityCell.CellAcquisitionDecoderState));
        Assert.NotEqual(0x21, fill.Bytes.Span[2]);
        var page = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: GsmCompatibilityCell.IdlePagingDecoderState));
        Assert.Equal(
            new byte[] { 0x31, 0x06, 0x21, 0x00, 0x08 },
            page.Bytes.Span[..5].ToArray());
        pending = new(0x16, 0, 23, 10);
        cell.Encode(new AsicChannelEncoderRequest(
            new byte[] { 0x16 },
            GsmCompatibilityCell.RandomAccessUplinkEncoderState));
        var assignment = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        Assert.Equal(
            new byte[] { 0x2d, 0x06, 0x3f },
            assignment.Bytes.Span[..3].ToArray());

        AssertIncomingSmsDedicatedSetup(cell);
        AssertIncomingSmsDelivery(cell);
        AssertIncomingSmsCounters(cell);
    }

    static void AssertIncomingSmsDedicatedSetup(GsmCompatibilityCell cell)
    {
        byte[] pagingResponseSabm =
        [
            0x01, 0x3f, 0x31,
            0x06, 0x27, 0x02, 0x00, 0x08, 0x92, 0x80, 0x10, 0x00, 0x00,
            0x00, 0x00,
            .. Enumerable.Repeat((byte)0x2b, 8),
        ];
        cell.Encode(pagingResponseSabm);
        var ua = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var cipheringModeCommand = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] cipheringModeComplete =
        [
            0x01, 0x20, 0x09, 0x06, 0x32,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(cipheringModeComplete);
        var cipheringCompleteRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var mmInformation = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var sapi3Sabm = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        Assert.Equal(new byte[] { 0x01, 0x73, 0x31 }, ua.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x00, 0x0d, 0x06, 0x35, 0x00 },
            cipheringModeCommand.Bytes.Span[..6].ToArray());
        Assert.Equal(
            new byte[] { 0x01, 0x21, 0x01 },
            cipheringCompleteRr.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x22, 0x29, 0x05, 0x32, 0x47 },
            mmInformation.Bytes.Span[..6].ToArray());
        Assert.Equal(
            new byte[] { 0x0f, 0x3f, 0x01 },
            sapi3Sabm.Bytes.Span[..3].ToArray());
    }

    static void AssertIncomingSmsDelivery(GsmCompatibilityCell cell)
    {
        byte[] sapi3Ua =
        [
            0x0f, 0x73, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(sapi3Ua);
        var firstCpDataSegment = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var secondCpDataSegment = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] mobileCpAck =
        [
            0x0d, 0x40, 0x09, 0x89, 0x04,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(mobileCpAck);
        var cpAckRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] mobileRpAck =
        [
            0x0d, 0x42, 0x29,
            0x89, 0x01, 0x07, 0x02, 0x40, 0x41, 0x03, 0x00, 0x01, 0x00,
            .. Enumerable.Repeat((byte)0x2b, 10),
        ];
        cell.Encode(mobileRpAck);
        var rpAckRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var networkCpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var release = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var idle = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));

        Assert.Equal(
            new byte[]
            {
                0x0f, 0x00, 0x53,
                0x09, 0x01, 0x21, 0x01, 0x40, 0x06, 0x91, 0x21, 0x43,
                0x65, 0x87, 0x09, 0x00, 0x16, 0x04, 0x07, 0x91, 0x55,
                0x15, 0x32,
            },
            firstCpDataSegment.Bytes.ToArray());
        Assert.Equal(
            new byte[]
            {
                0x0f, 0x02, 0x41,
                0xf4, 0x00, 0x00, 0x62, 0x70, 0x60, 0x41, 0x35, 0x92,
                0x23, 0x05, 0xe8, 0x32, 0x9b, 0xfd, 0x06,
                0x2b, 0x2b, 0x2b, 0x2b,
            },
            secondCpDataSegment.Bytes.ToArray());
        Assert.Equal(new byte[] { 0x0d, 0x21, 0x01 }, cpAckRr.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x0d, 0x41, 0x01 }, rpAckRr.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x0f, 0x44, 0x09, 0x09, 0x04 },
            networkCpAck.Bytes.Span[..5].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x24, 0x0d, 0x06, 0x0d, 0x00 },
            release.Bytes.Span[..6].ToArray());
        Assert.Equal(0x06, idle.Bytes.Span[1]);
        Assert.Contains(idle.Bytes.Span[2], new byte[] { 0x1a, 0x1b, 0x1c });
    }

    static void AssertIncomingSmsCounters(GsmCompatibilityCell cell)
    {
        Assert.Equal(1, cell.QueuedIncomingSmsCount);
        Assert.Equal(1, cell.PagingRequestCount);
        Assert.Equal(1, cell.PagingResponseCount);
        Assert.Equal(1, cell.MobileTerminatedSapi3EstablishmentCount);
        Assert.Equal(1, cell.MobileTerminatedSmsCount);
        Assert.Equal(1, cell.MobileTerminatedSmsCpAckCount);
        Assert.Equal(1, cell.MobileTerminatedSmsRpAckCount);
        Assert.Equal(1, cell.DeliveredIncomingSmsCount);
        Assert.True(cell.Registered);
        Assert.False(cell.MmConnectionActive);
    }

    [Fact]
    public void UnansweredIncomingSmsPagingStopsAfterEightGroupBursts()
    {
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            incomingPagingReadySource: () => true);
        CompleteRegistration(cell);
        cell.QueueIncomingSms("5551234", "hello");

        var blocks = Enumerable.Range(0, 9)
            .Select(_ => cell.DecodeControlChannel(
                new AsicControlChannelDecodeRequest(
                    new byte[AsicChannelDecoder.ControlChannelInputLength],
                    FirmwareState: GsmCompatibilityCell.IdlePagingDecoderState)))
            .ToArray();

        Assert.All(blocks[..8], block =>
            Assert.Equal(0x21, block.Bytes.Span[2]));
        Assert.NotEqual(0x21, blocks[8].Bytes.Span[2]);
        Assert.Equal(8, cell.PagingRequestCount);
        Assert.Equal(0, cell.PagingResponseCount);
    }

    [Fact]
    public void IncomingCallPagesAndDeliversCcSetupAfterCiphering()
    {
        GsmRandomAccessReference? pending = null;
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            randomAccessReferenceSource: () => pending,
            incomingPagingReadySource: () => true);
        CompleteRegistration(cell);
        cell.QueueIncomingCall("+5551234");

        var page = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: GsmCompatibilityCell.IdlePagingDecoderState));
        Assert.Equal(
            new byte[] { 0x31, 0x06, 0x21, 0x00, 0x08 },
            page.Bytes.Span[..5].ToArray());
        pending = new(0x16, 0, 23, 10);
        cell.Encode(new AsicChannelEncoderRequest(
            new byte[] { 0x16 },
            GsmCompatibilityCell.RandomAccessUplinkEncoderState));
        var assignment = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        Assert.Equal(
            new byte[] { 0x2d, 0x06, 0x3f },
            assignment.Bytes.Span[..3].ToArray());

        AssertIncomingCallSetup(cell);
        AssertIncomingCallTrafficAssignment(cell);
        AssertIncomingCallCounters(cell);
        AssertIncomingCallRelease(cell);
    }

    static void AssertIncomingCallSetup(GsmCompatibilityCell cell)
    {
        byte[] pagingResponseSabm =
        [
            0x01, 0x3f, 0x31,
            0x06, 0x27, 0x02, 0x00, 0x08, 0x92, 0x80, 0x10, 0x00, 0x00,
            0x00, 0x00,
            .. Enumerable.Repeat((byte)0x2b, 8),
        ];
        cell.Encode(pagingResponseSabm);
        var ua = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var cipheringModeCommand = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] cipheringModeComplete =
        [
            0x01, 0x20, 0x09, 0x06, 0x32,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(cipheringModeComplete);
        var cipheringCompleteRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var setup = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        Assert.Equal(new byte[] { 0x01, 0x73, 0x31 }, ua.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x00, 0x0d, 0x06, 0x35, 0x00 },
            cipheringModeCommand.Bytes.Span[..6].ToArray());
        Assert.Equal(
            new byte[] { 0x01, 0x21, 0x01 },
            cipheringCompleteRr.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[]
            {
                0x03, 0x22, 0x45, 0x03, 0x05, 0x04, 0x04, 0x60,
                0x02, 0x00, 0x81, 0x34, 0x01, 0x5c, 0x05, 0x91,
                0x55, 0x15,
                0x32, 0xf4,
            },
            setup.Bytes.Span[..20].ToArray());
    }

    static void AssertIncomingCallTrafficAssignment(GsmCompatibilityCell cell)
    {
        byte[] callConfirmed =
        [
            0x01, 0x42, 0x09, 0x03, 0x08,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(callConfirmed);
        var trafficAssignment = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] assignedLinkSabm =
        [
            0x01, 0x3f, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(assignedLinkSabm);
        var assignedLinkUa = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                GsmCompatibilityCell.DedicatedDownlinkDecoderState));
        byte[] assignmentComplete =
        [
            0x01, 0x00, 0x0d, 0x06, 0x29, 0x00,
            .. Enumerable.Repeat((byte)0x2b, 17),
        ];
        cell.Encode(new AsicChannelEncoderRequest(
            assignmentComplete,
            GsmCompatibilityCell.TrafficUplinkEncoderState));
        var assignmentCompleteRr = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                GsmCompatibilityCell.DedicatedDownlinkDecoderState));
        byte[] alerting =
        [
            0x01, 0x02, 0x09, 0x83, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(alerting);
        var alertingRr = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                GsmCompatibilityCell.DedicatedDownlinkDecoderState));
        byte[] connect =
        [
            0x01, 0x04, 0x09, 0x83, 0x07,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(connect);
        var connectAcknowledge = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                GsmCompatibilityCell.DedicatedDownlinkDecoderState));

        Assert.Equal(
            new byte[]
            {
                0x03, 0x44, 0x21,
                0x06, 0x2e, 0x0a, 0x03, 0xcf, 0x07, 0x63, 0x01,
            },
            trafficAssignment.Bytes.Span[..11].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x73, 0x01 }, assignedLinkUa.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x21, 0x01 }, assignmentCompleteRr.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x41, 0x01 }, alertingRr.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x60, 0x09, 0x03, 0x0f },
            connectAcknowledge.Bytes.Span[..5].ToArray());
    }

    static void AssertIncomingCallCounters(GsmCompatibilityCell cell)
    {
        Assert.Equal(1, cell.QueuedIncomingCallCount);
        Assert.Equal(1, cell.PagingRequestCount);
        Assert.Equal(1, cell.PagingResponseCount);
        Assert.Equal(1, cell.MobileTerminatedCallSetupCount);
        Assert.Equal(1, cell.MobileTerminatedCallConfirmedCount);
        Assert.Equal(1, cell.MobileTerminatedCallConnectCount);
        Assert.Equal(1, cell.MobileTerminatedTrafficAssignmentCount);
        Assert.Equal(1, cell.MobileTerminatedTrafficAssignmentDeliveredCount);
        Assert.Equal(1, cell.MobileTerminatedTrafficAssignmentCompleteCount);
        Assert.True(cell.MmConnectionActive);
    }

    static void AssertIncomingCallRelease(GsmCompatibilityCell cell)
    {
        byte[] disconnect =
        [
            0x01, 0x26, 0x09, 0x83, 0x25,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(disconnect);
        var release = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] releaseComplete =
        [
            0x01, 0x48, 0x09, 0x83, 0x2a,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(releaseComplete);
        var channelRelease = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(new byte[] { 0x03, 0x82, 0x09, 0x03, 0x2d }, release.Bytes.Span[..5].ToArray());
        Assert.Equal(new byte[] { 0x03, 0xa4, 0x0d, 0x06, 0x0d }, channelRelease.Bytes.Span[..5].ToArray());

        var releaseBoundary = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: GsmCompatibilityCell.CellAcquisitionDecoderState));

        Assert.True(releaseBoundary.Success);
        Assert.Equal(1, cell.MobileDedicatedReleaseCount);
        Assert.False(cell.MmConnectionActive);
    }

    [Fact]
    public void ImmediateAssignmentRejectsInvalidCellParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GsmCompatibilityCell.BuildImmediateAssignment(0, -1, 0, -49));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GsmCompatibilityCell.BuildImmediateAssignment(0, 0, 0x40, -49));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GsmCompatibilityCell.BuildImmediateAssignment(0, 0, 0, -50));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GsmCompatibilityCell.BuildImmediateAssignment(new(0, 32, 0, 0), 0, -49));
    }

    [Fact]
    public void SmsCmServiceExchangeReachesActiveMmConnection()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);
        byte[] cmServiceSabm =
        [
            0x01, 0x3f, 0x41,
            0x05, 0x24, 0x74, 0x03, 0x33, 0x19, 0x81, 0x08,
            0x09, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
            0x2b, 0x2b, 0x2b, 0x2b,
        ];

        cell.Encode(cmServiceSabm);
        var ua = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var cipheringModeCommand = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] cipheringModeComplete =
        [
            0x01, 0x20, 0x09, 0x06, 0x32,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(cipheringModeComplete);
        var rr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] sapi3Sabm =
        [
            0x0d, 0x3f, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(sapi3Sabm);
        var sapi3Ua = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(new byte[] { 0x01, 0x73, 0x41 }, ua.Bytes.Span[..3].ToArray());
        Assert.Equal(cmServiceSabm[3..19], ua.Bytes.Span[3..19].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x00, 0x0d, 0x06, 0x35, 0x00 },
            cipheringModeCommand.Bytes.Span[..6].ToArray());
        Assert.Equal(new byte[] { 0x01, 0x21, 0x01 }, rr.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x0d, 0x73, 0x01 }, sapi3Ua.Bytes.Span[..3].ToArray());
        Assert.True(cell.MmConnectionActive);
        Assert.Equal(2, cell.SabmCount);
        Assert.Equal(2, cell.UaCount);
        Assert.Equal(1, cell.CmServiceRequestCount);
        Assert.Equal(1, cell.CipheringModeCommandCount);
        Assert.Equal(1, cell.CipheringModeCompleteCount);
    }

    [Fact]
    public void SegmentedSmsCpDataIsAcknowledgedThroughRpAckAndRelease()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);
        EstablishSmsMmConnection(cell);
        byte[] cpData =
        [
            0x39, 0x01, 0x1b, 0x00, 0x01, 0x00, 0x06, 0x91, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x10, 0x11, 0x05, 0x0d, 0x81, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x21, 0xf3, 0x00, 0x00, 0xa7, 0x01, 0x41,
        ];
        byte[] firstSegment =
        [
            0x0d, 0x00, 0x53,
            .. cpData[..20],
        ];

        cell.Encode(firstSegment);
        var firstRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] secondSegment =
        [
            0x0d, 0x02, 0x29,
            .. cpData[20..],
            .. Enumerable.Repeat((byte)0x2b, 10),
        ];
        cell.Encode(secondSegment);
        var secondRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var cpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var rpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] mobileCpAck =
        [
            0x0d, 0x44, 0x09, 0x39, 0x04,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(mobileCpAck);
        var finalRr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var release = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var idle = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));

        Assert.Equal(new byte[] { 0x0d, 0x21, 0x01 }, firstRr.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x0d, 0x41, 0x01 }, secondRr.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x0f, 0x40, 0x09, 0xb9, 0x04 }, cpAck.Bytes.Span[..5].ToArray());
        Assert.Equal(
            new byte[] { 0x0f, 0x42, 0x15, 0xb9, 0x01, 0x02, 0x03, 0x01 },
            rpAck.Bytes.Span[..8].ToArray());
        Assert.Equal(new byte[] { 0x0d, 0x61, 0x01 }, finalRr.Bytes.Span[..3].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x22, 0x0d, 0x06, 0x0d, 0x00 },
            release.Bytes.Span[..6].ToArray());
        Assert.Equal(new byte[] { 0x49, 0x06, 0x1b }, idle.Bytes.Span[..3].ToArray());
        Assert.Equal(4, cell.DedicatedUplinkInformationCount);
        Assert.Equal(1, cell.SmsCpDataCount);
        Assert.Equal(1, cell.SmsRpDataCount);
        Assert.Equal(1, cell.SmsCpAckCount);
        Assert.Equal(1, cell.SmsRpAckCount);
        Assert.Equal(1, cell.MobileSmsCpAckCount);
        Assert.Equal(0, cell.MobileDedicatedReleaseCount);
    }

    [Fact]
    public void DecodedSmsSubmitWaitsForHostAcceptanceBeforeRpAck()
    {
        List<GsmOutgoingNetworkRequest> requests = [];
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            outgoingNetworkRequest: requests.Add);
        EstablishSmsMmConnection(cell);
        byte[] cpData =
        [
            0x39, 0x01, 0x1b, 0x00, 0x01, 0x00, 0x06, 0x91, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x10, 0x11, 0x05, 0x0d, 0x81, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x21, 0xf3, 0x00, 0x00, 0xa7, 0x01, 0x41,
        ];
        byte[] firstSegment =
        [
            0x0d, 0x00, 0x53,
            .. cpData[..20],
        ];
        cell.Encode(firstSegment);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] secondSegment =
        [
            0x0d, 0x02, 0x29,
            .. cpData[20..],
            .. Enumerable.Repeat((byte)0x2b, 10),
        ];
        cell.Encode(secondSegment);

        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var cpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var waitingFill = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var request = Assert.Single(requests);

        Assert.Equal(GsmNetworkRequestKind.Sms, request.Kind);
        Assert.Equal("1234567890123", request.NormalizedDestination);
        Assert.Equal("A", request.SmsText);
        Assert.Equal(
            new byte[] { 0x0f, 0x40, 0x09, 0xb9, 0x04 },
            cpAck.Bytes.Span[..5].ToArray());
        Assert.Equal(
            GsmCompatibilityCell.BuildLapdmFillFrame(),
            waitingFill.Bytes.ToArray());
        Assert.False(cell.ResolveNetworkRequest(new(
            Guid.NewGuid(),
            GsmNetworkRequestDecision.Accept)));
        Assert.True(cell.ResolveNetworkRequest(new(
            request.RequestId,
            GsmNetworkRequestDecision.Accept)));

        var rpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(
            new byte[] { 0x0f, 0x42, 0x15, 0xb9, 0x01, 0x02, 0x03, 0x01 },
            rpAck.Bytes.Span[..8].ToArray());
        Assert.Equal(1, cell.OutgoingSmsRequestCount);
        Assert.Equal(1, cell.SmsRpAckCount);
        Assert.Equal(0, cell.SmsRpErrorCount);
    }

    [Fact]
    public void RejectedSmsSubmitReceivesRpError()
    {
        List<GsmOutgoingNetworkRequest> requests = [];
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            outgoingNetworkRequest: requests.Add);
        EstablishSmsMmConnection(cell);
        byte[] cpData =
        [
            0x39, 0x01, 0x1b, 0x00, 0x01, 0x00, 0x06, 0x91, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x10, 0x11, 0x05, 0x0d, 0x81, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x21, 0xf3, 0x00, 0x00, 0xa7, 0x01, 0x41,
        ];
        byte[] firstSegment =
        [
            0x0d, 0x00, 0x53,
            .. cpData[..20],
        ];
        cell.Encode(firstSegment);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] secondSegment =
        [
            0x0d, 0x02, 0x29,
            .. cpData[20..],
            .. Enumerable.Repeat((byte)0x2b, 10),
        ];
        cell.Encode(secondSegment);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var request = Assert.Single(requests);

        Assert.True(cell.ResolveNetworkRequest(new(
            request.RequestId,
            GsmNetworkRequestDecision.Reject)));
        var rpError = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(
            new byte[]
            {
                0x0f, 0x42, 0x1d,
                0xb9, 0x01, 0x04, 0x05, 0x01, 0x01, 0x15,
            },
            rpError.Bytes.Span[..10].ToArray());
        Assert.Equal(0, cell.SmsRpAckCount);
        Assert.Equal(1, cell.SmsRpErrorCount);
    }

    [Fact]
    public void DecodedCallSetupWaitsForHostAcceptanceBeforeCallProceeding()
    {
        List<GsmOutgoingNetworkRequest> requests = [];
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            outgoingNetworkRequest: requests.Add);
        EstablishCallMmConnection(cell);
        GsmOutgoingNetworkRequest request = AssertDecodedOutgoingCallRequest(
            cell,
            requests);
        AssertAcceptedOutgoingCallProgress(cell, request);
    }

    static GsmOutgoingNetworkRequest AssertDecodedOutgoingCallRequest(
        GsmCompatibilityCell cell,
        List<GsmOutgoingNetworkRequest> requests)
    {
        Assert.True(cell.MmConnectionActive);
        byte[] setup =
        [
            0x01, 0x22, 0x35,
            0x03, 0x45, 0x04, 0x04, 0x60, 0x02, 0x00, 0x81, 0x5e,
            0x03, 0x81, 0x21, 0xf3,
            .. Enumerable.Repeat((byte)0x2b, 7),
        ];
        Assert.True(GsmCompatibilityCell.TryDecodeMobileOriginatedCallSetup(
            setup.AsSpan(3, 13),
            out var decodedDestination,
            out var decodedInternational));
        Assert.Equal("123", decodedDestination);
        Assert.False(decodedInternational);

        cell.Encode(setup);
        var receiveReady = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var waitingFill = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        Assert.Equal(
            new byte[] { 0x01, 0x41, 0x01 },
            receiveReady.Bytes.Span[..3].ToArray());
        Assert.Equal(
            GsmCompatibilityCell.BuildLapdmFillFrame(),
            waitingFill.Bytes.ToArray());
        var request = Assert.Single(requests);

        Assert.Equal(GsmNetworkRequestKind.Call, request.Kind);
        Assert.Equal("123", request.NormalizedDestination);
        Assert.Equal(string.Empty, request.SmsText);
        return request;
    }

    static void AssertAcceptedOutgoingCallProgress(
        GsmCompatibilityCell cell,
        GsmOutgoingNetworkRequest request)
    {
        Assert.True(cell.ResolveNetworkRequest(new(
            request.RequestId,
            GsmNetworkRequestDecision.Accept)));

        var proceeding = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(
            new byte[] { 0x03, 0x42, 0x09, 0x83, 0x02 },
            proceeding.Bytes.Span[..5].ToArray());
        byte[] proceedingAcknowledgement =
        [
            0x03, 0x41, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(proceedingAcknowledgement);
        var alerting = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(
            new byte[] { 0x03, 0x44, 0x09, 0x83, 0x01 },
            alerting.Bytes.Span[..5].ToArray());
        byte[] alertingAcknowledgement =
        [
            0x03, 0x61, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(alertingAcknowledgement);
        var connect = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);

        Assert.Equal(
            new byte[] { 0x03, 0x46, 0x09, 0x83, 0x07 },
            connect.Bytes.Span[..5].ToArray());
        Assert.Equal(1, cell.OutgoingCallRequestCount);
        Assert.Equal(1, cell.CallProceedingCount);
        Assert.Equal(1, cell.CallAlertingCount);
        Assert.Equal(1, cell.CallConnectCount);
    }

    [Fact]
    public void SmsMemoryAvailableNotificationReceivesRpAck()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);
        EstablishSmsMmConnection(cell);
        byte[] cpDataWithRpSmma =
        [
            0x0d, 0x00, 0x15,
            0x09, 0x01, 0x02, 0x06, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 15),
        ];

        cell.Encode(cpDataWithRpSmma);
        var rr = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var cpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var rpAck = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] mobileCpAck =
        [
            0x0d, 0x42, 0x09, 0x09, 0x04,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(mobileCpAck);
        var idle = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));

        Assert.Equal(new byte[] { 0x0d, 0x21, 0x01 }, rr.Bytes.Span[..3].ToArray());
        Assert.Equal(new byte[] { 0x0f, 0x20, 0x09, 0x89, 0x04 }, cpAck.Bytes.Span[..5].ToArray());
        Assert.Equal(
            new byte[] { 0x0f, 0x22, 0x15, 0x89, 0x01, 0x02, 0x03, 0x01 },
            rpAck.Bytes.Span[..8].ToArray());
        Assert.Equal(1, cell.SmsCpDataCount);
        Assert.Equal(0, cell.SmsRpDataCount);
        Assert.Equal(1, cell.SmsRpSmmaCount);
        Assert.Equal(1, cell.SmsCpAckCount);
        Assert.Equal(1, cell.SmsRpAckCount);
        Assert.Equal(new byte[] { 0x49, 0x06, 0x1b }, idle.Bytes.Span[..3].ToArray());
        Assert.Equal(1, cell.MobileSmsCpAckCount);
        Assert.Equal(1, cell.MobileDedicatedReleaseCount);
        Assert.Equal(0, cell.ChannelReleaseCount);
        Assert.True(cell.Registered);
        Assert.False(cell.MmConnectionActive);
    }

    [Fact]
    public void LocationUpdatingExchangeUsesDedicatedStatesAndQueuesRelease()
    {
        var cell = new GsmCompatibilityCell(-49, 0x0800);
        byte[] sabm =
        [
            0x01, 0x3f, 0x49,
            0x05, 0x08, 0x70, 0xff, 0xff, 0xff, 0xff, 0xff,
            0x33, 0x08, 0x09, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
            0x2b, 0x2b,
        ];

        cell.Encode(new AsicChannelEncoderRequest(
            sabm,
            FirmwareState: 8));
        Assert.Equal(0, cell.SabmCount);
        cell.Encode(sabm);
        var unrelatedState = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));
        var nextSacch = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));
        var ua = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var accept = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] rr = [0x03, 0x21, 0x01, .. Enumerable.Repeat((byte)0x2b, 20)];
        cell.Encode(new AsicChannelEncoderRequest(
            rr,
            GsmCompatibilityCell.DedicatedUplinkEncoderState));
        var release = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var fill = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        var idle = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));

        Assert.True(unrelatedState.Success);
        Assert.Equal(
            GsmCompatibilityCell.BuildSacchSystemInformation6(
                GsmCompatibilityCell.EncodeLocationAreaIdentity(
                    SwSimCard.DefaultImsi,
                    lac: 1),
                GsmCompatibilityCell.DefaultBsic),
            unrelatedState.Bytes.ToArray());
        Assert.Equal(
            GsmCompatibilityCell.BuildSacchSystemInformation5(),
            nextSacch.Bytes.ToArray());
        Assert.True(ua.Success);
        Assert.Equal(0x01, ua.Bytes.Span[0]);
        Assert.Equal(0x73, ua.Bytes.Span[1]);
        Assert.Equal(sabm[2..21], ua.Bytes.Span[2..21].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x00, 0x1d, 0x05, 0x02, 0x00, 0xf1, 0x10, 0x00, 0x01 },
            accept.Bytes.Span[..10].ToArray());
        Assert.Equal(
            new byte[] { 0x03, 0x02, 0x0d, 0x06, 0x0d, 0x00 },
            release.Bytes.Span[..6].ToArray());
        Assert.Equal(GsmCompatibilityCell.BuildLapdmFillFrame(), fill.Bytes.ToArray());
        Assert.Equal(new byte[] { 0x49, 0x06, 0x1b }, idle.Bytes.Span[..3].ToArray());
        Assert.True(cell.Registered);
        Assert.Equal(1, cell.SabmCount);
        Assert.Equal(1, cell.UaCount);
        Assert.Equal(1, cell.LocationUpdatingAcceptCount);
        Assert.Equal(1, cell.ReceiveReadyCount);
        Assert.Equal(1, cell.ChannelReleaseCount);
    }

    static void EstablishSmsMmConnection(GsmCompatibilityCell cell)
    {
        byte[] cmServiceSabm =
        [
            0x01, 0x3f, 0x41,
            0x05, 0x24, 0x74, 0x03, 0x33, 0x19, 0x81, 0x08,
            0x09, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
            0x2b, 0x2b, 0x2b, 0x2b,
        ];
        cell.Encode(cmServiceSabm);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] cipheringModeComplete =
        [
            0x01, 0x20, 0x09, 0x06, 0x32,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(cipheringModeComplete);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] sapi3Sabm =
        [
            0x0d, 0x3f, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(sapi3Sabm);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
    }

    static void EstablishCallMmConnection(GsmCompatibilityCell cell)
    {
        byte[] cmServiceSabm =
        [
            0x01, 0x3f, 0x41,
            0x05, 0x24, 0x71, 0x03, 0x33, 0x19, 0x81, 0x08,
            0x09, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
            0x2b, 0x2b, 0x2b, 0x2b,
        ];
        cell.Encode(cmServiceSabm);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] cipheringModeComplete =
        [
            0x01, 0x20, 0x09, 0x06, 0x32,
            .. Enumerable.Repeat((byte)0x2b, 18),
        ];
        cell.Encode(cipheringModeComplete);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
    }

    internal static void CompleteRegistration(GsmCompatibilityCell cell)
    {
        byte[] locationUpdatingSabm =
        [
            0x01, 0x3f, 0x49,
            0x05, 0x08, 0x70, 0xff, 0xff, 0xff, 0xff, 0xff,
            0x33, 0x08, 0x09, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
            0x2b, 0x2b,
        ];
        cell.Encode(locationUpdatingSabm);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        byte[] rr =
        [
            0x03, 0x21, 0x01,
            .. Enumerable.Repeat((byte)0x2b, 20),
        ];
        cell.Encode(rr);
        _ = cell.DecodeControlChannel(
            new byte[AsicChannelDecoder.ControlChannelInputLength]);
        _ = cell.DecodeControlChannel(
            new AsicControlChannelDecodeRequest(
                new byte[AsicChannelDecoder.ControlChannelInputLength],
                FirmwareState: 6));
    }

    [Fact]
    public async Task ConcurrentDecodeEncodeAndQueueCallsAreSerializedByOneOwner()
    {
        const int Iterations = 500;
        using var worker = new MiaWorker("test GSM cell owner");
        var cell = new GsmCompatibilityCell(
            -49,
            0x0800,
            SwSimCard.DefaultImsi,
            worker: worker);
        var decodeInput = new byte[AsicChannelDecoder.ControlChannelInputLength];
        var randomAccessBurst = new byte[] { 0x2a };

        var decodeTask = Task.Run(() =>
        {
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                cell.DecodeControlChannel(decodeInput);
            }
        });
        var encodeTask = Task.Run(() =>
        {
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                cell.Encode(new AsicChannelEncoderRequest(
                    randomAccessBurst,
                    GsmCompatibilityCell.RandomAccessUplinkEncoderState));
            }
        });
        var queueSmsTask = Task.Run(() =>
        {
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                cell.QueueIncomingSms("5551234", "hi");
            }
        });
        var queueCallTask = Task.Run(() =>
        {
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                cell.QueueIncomingCall("5555678");
            }
        });

        await Task.WhenAll(decodeTask, encodeTask, queueSmsTask, queueCallTask);

        Assert.Equal(Iterations, cell.ControlChannelDecodeCount);
        Assert.Equal(Iterations, cell.RandomAccessRequestCount);
        Assert.Equal(Iterations, cell.QueuedIncomingSmsCount);
        Assert.Equal(Iterations, cell.QueuedIncomingCallCount);
    }

    static AsicRfTransaction CreateTransaction(short arfcn)
    {
        var normalized = arfcn + 216;
        var raw = unchecked((ushort)(0xdd78 + 0x28 * normalized));
        var high = (byte)(raw >> 8);
        var encodedHigh = (byte)((high & 0x3f) | ((high & 0x80) >> 1));
        var bytes = new byte[AsicRfFrontend.TransactionSize];
        bytes[7] = (byte)raw;
        bytes[8] = encodedHigh;
        bytes[9] = (byte)raw;
        bytes[10] = encodedHigh;
        return new(0, bytes);
    }
}
