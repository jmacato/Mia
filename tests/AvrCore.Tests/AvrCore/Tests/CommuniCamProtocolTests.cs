// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace AvrCore.Tests;

public sealed class CommuniCamProtocolTests
{
    [Fact]
    public void OversizedSettingsUploadIsRejectedAndCanBeRetried()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        var fragment = new byte[4096];
        Assert.Equal(0x90, peer.PutFragment(fragment));
        Assert.Equal(0x90, peer.PutFragment(fragment));
        Assert.Equal(0xcd, peer.PutFragment(fragment));
        peer.Configure(640, 480);
        Assert.NotEmpty(peer.GetCameraInfo());
    }

    [Theory]
    [InlineData(80, 60)]
    [InlineData(160, 120)]
    [InlineData(320, 240)]
    [InlineData(640, 480)]
    public void CameraInfoSettingsSelectEveryMca25NativeMode(
        int width,
        int height)
    {
        using var peer = new CommuniCamProtocolTestPeer();

        peer.Configure(width, height);
        peer.CaptureAndSave();
        XDocument info = ParseXml(peer.GetCameraInfo());
        XElement memory = Assert.Single(info.Descendants("memory"));

        Assert.Equal("9", (string?)memory.Attribute("free-images"));
        Assert.Equal("1", (string?)memory.Attribute("stored-images"));
        Assert.InRange((int)memory.Attribute("free")!, 0, 590);
        XDocument properties = ParseXml(peer.GetProperties("0000001"));
        XElement native = Assert.Single(properties.Descendants("native-image"));
        Assert.Equal(
            FormattableString.Invariant($"{width}*{height}"),
            (string?)native.Attribute("pixel-size"));
        Assert.Equal("JPEG", (string?)native.Attribute("encoding"));
    }

    [Fact]
    public void CameraInfoStartsWithPhysicalEmptyMemoryValues()
    {
        using var peer = new CommuniCamProtocolTestPeer();

        XDocument info = ParseXml(peer.GetCameraInfo());
        XElement memory = Assert.Single(info.Descendants("memory"));

        Assert.Equal("591", (string?)memory.Attribute("free"));
        Assert.Equal("10", (string?)memory.Attribute("free-images"));
        Assert.Equal("0", (string?)memory.Attribute("stored-images"));
        Assert.Equal("10", (string?)memory.Attribute("fun-layer"));
    }

    [Fact]
    public void CameraInfoSettingsRejectUnsupportedFixedAndNativeFormats()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        const string InvalidMonitoring =
            "<camera-settings><monitoring-format encoding=\"JPEG\" pixel-size=\"80*60\" color-depth=\"8\"/><thumbnail-format encoding=\"EBMP\" pixel-size=\"101*80\" color-depth=\"8\"/><native-format encoding=\"\" pixel-size=\"640*480\"/></camera-settings>";
        const string InvalidNative =
            "<camera-settings><monitoring-format encoding=\"EBMP\" pixel-size=\"80*60\" color-depth=\"8\"/><thumbnail-format encoding=\"EBMP\" pixel-size=\"101*80\" color-depth=\"8\"/><native-format encoding=\"\" pixel-size=\"800*600\"/></camera-settings>";

        Assert.Equal(0xc0, peer.ConfigureRaw(InvalidMonitoring));
        Assert.Equal(0xc0, peer.ConfigureRaw(InvalidNative));
        Assert.Equal(0xa0, peer.ConfigureWithMultipleBodyHeaders(640, 480));
    }

    [Fact]
    public void MonitoringReturnsPhysicalEbmpLayoutAtOneFramePerSecond()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        const string Command =
            "<monitoring-command version=\"1.0\" take-pic=\"YES\" zoom=\"10\"/>";

        long firstRequestCycle = peer.Cycles;
        byte[] first = peer.GetMonitoring(Command, out byte[] firstResponse);
        long secondRequestCycle = peer.Cycles;
        byte[] second = peer.GetMonitoring(Command, out _);
        long secondCompletedCycle = peer.Cycles;

        AssertEbmp(first, 80, 60);
        Assert.Equal(first, second);
        Assert.Equal(0x90, firstResponse[0]);
        Assert.Equal(0xc3, firstResponse[3]);
        Assert.Equal(
            first.Length,
            BinaryPrimitives.ReadInt32BigEndian(firstResponse.AsSpan(4, 4)));
        Assert.True(secondRequestCycle > firstRequestCycle);
        Assert.True(
            secondCompletedCycle - firstRequestCycle >=
            MiaSystemClock.AsicCyclesPerSecond);
        Assert.Equal(2, peer.Status.FramesSent);
        Assert.Equal(MiaCommuniCamState.Streaming, peer.Status.State);
    }

    [Fact]
    public void MonitoringWaitsForARealFrameInsteadOfReturningBlackFallback()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        peer.DelayFrameAvailability(3);
        long requestCycle = peer.Cycles;

        byte[] image = peer.GetMonitoring(
            "<monitoring-command version=\"1.0\" take-pic=\"YES\" zoom=\"10\"/>",
            out _);

        AssertEbmp(image, 80, 60);
        Assert.Equal(4, peer.FrameRequestCount);
        Assert.True(
            peer.Cycles - requestCycle >=
            3 * MiaSystemClock.AsicCyclesPerSecond / 10);
    }

    [Fact]
    public void ThumbnailReturnsPhysical101By80EbmpFromCapturedSnapshot()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        peer.Configure(640, 480);
        peer.CaptureAndSave();

        byte[] thumbnail = peer.GetThumbnail("0000001");
        byte[] expected = MiaCommuniCamEbmpEncoder.Encode(
            peer.SourceFrame,
            101,
            80);

        AssertEbmp(thumbnail, 101, 80);
        Assert.Equal(expected, thumbnail);
    }

    [Theory]
    [InlineData(80, 60)]
    [InlineData(160, 120)]
    [InlineData(320, 240)]
    [InlineData(640, 480)]
    public void NativeReturnsValidJpegForEveryMca25Mode(
        int width,
        int height)
    {
        using var peer = new CommuniCamProtocolTestPeer();
        peer.Configure(width, height);
        peer.CaptureAndSave();

        byte[] jpeg = peer.GetNative("0000001");

        AssertJpeg(jpeg, width, height);
        Assert.Equal(
            MiaCommuniCamJpegEncoder.Encode(peer.SourceFrame, width, height),
            jpeg);
    }

    [Theory]
    [InlineData(80, 60)]
    [InlineData(160, 120)]
    [InlineData(320, 240)]
    [InlineData(640, 480)]
    public void NativeFreshCaptureSupportsEverySendPixelSize(
        int width,
        int height)
    {
        using var peer = new CommuniCamProtocolTestPeer();
        _ = peer.GetMonitoring(
            "<monitoring-command version=\"1.0\" take-pic=\"YES\" zoom=\"10\"/>",
            out _);

        byte[] jpeg = peer.GetFreshNative(width, height);

        AssertJpeg(jpeg, width, height);
        Assert.Equal(
            MiaCommuniCamJpegEncoder.Encode(peer.SourceFrame, width, height),
            jpeg);
    }

    [Fact]
    public void NativeFreshCaptureRejectsUnsupportedSendPixelSize()
    {
        using var peer = new CommuniCamProtocolTestPeer();

        Assert.Equal(0xc0, peer.SendFreshNativeRequest("800*600"));
    }

    [Fact]
    public void NativeAndThumbnailStayBoundToTheSavedSnapshot()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        peer.Configure(640, 480);
        peer.CaptureAndSave();
        byte[] thumbnail = peer.GetThumbnail("0000001");
        byte[] native = peer.GetNative("0000001");

        peer.PublishSolidFrame(0xe0);
        _ = peer.GetMonitoring(
            "<monitoring-command version=\"1.0\" take-pic=\"YES\" zoom=\"10\"/>",
            out _);

        Assert.Equal(thumbnail, peer.GetThumbnail("0000001"));
        Assert.Equal(native, peer.GetNative("0000001"));
    }

    [Fact]
    public void ObexListingPropertiesVariantAndDeleteUseStableHandles()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        peer.Configure(640, 480);
        peer.CaptureAndSave();
        peer.CaptureAndSave();

        string listing = Encoding.ASCII.GetString(peer.GetListing());
        string properties = Encoding.ASCII.GetString(
            peer.GetProperties("0000001"));
        byte[] variant = peer.GetVariant("0000001", 160, 120);

        Assert.Contains("handle=\"0000001\"", listing, StringComparison.Ordinal);
        Assert.Contains("handle=\"0000002\"", listing, StringComparison.Ordinal);
        Assert.Contains("<image-property", properties, StringComparison.Ordinal);
        Assert.Contains("<native-image", properties, StringComparison.Ordinal);
        Assert.Contains("<variant-image", properties, StringComparison.Ordinal);
        AssertJpeg(variant, 160, 120);
        Assert.Equal(0xa0, peer.Delete("0000001"));
        Assert.Equal(0xc4, peer.Delete("0000001"));
        listing = Encoding.ASCII.GetString(peer.GetListing());
        Assert.DoesNotContain("0000001", listing, StringComparison.Ordinal);
        Assert.Contains("0000002", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void ObexFragmentationAbortDisconnectAndReconnectClearState()
    {
        using var peer = new CommuniCamProtocolTestPeer();

        byte[] splitResponse = peer.SendPacketWithSplitWire(
            CommuniCamProtocolTestPeer.BuildCameraInfoGetPacket());
        Assert.Equal(0xa0, splitResponse[0]);
        peer.ConfigureFragmented(320, 240);
        peer.CaptureAndSave();
        Assert.Equal(0x90, peer.BeginThumbnail("0000001")[0]);
        Assert.Equal(0xa0, peer.Abort());
        Assert.Equal(0xa0, peer.SendContinuation()[0]);
        Assert.Equal(0x90, peer.BeginFragmentedSettings(640, 480)[0]);
        Assert.Equal(0xc9, peer.SendContinuation()[0]);
        Assert.Equal(0xc0, peer.SendMalformedHeader()[0]);
        Assert.Equal(0xa0, peer.DisconnectObex());
        Assert.Equal(
            0xc1,
            peer.SendPacketWithSplitWire(
                CommuniCamProtocolTestPeer.BuildCameraInfoGetPacket())[0]);
        Assert.Equal(0xa0, peer.ConnectObex());
        Assert.Contains(
            "stored-images=\"1\"",
            Encoding.ASCII.GetString(peer.GetCameraInfo()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CameraChannelCanCloseAndReopenWithoutUnpluggingOrLosingPictures()
    {
        using var peer = new CommuniCamProtocolTestPeer();
        peer.Configure(320, 240);
        peer.CaptureAndSave();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(0x90, peer.BeginFragmentedSettings(640, 480)[0]);
            Assert.Equal("F981730160F9", Convert.ToHexString(peer.SendChannelControl(0x53)));
            Assert.NotEqual(MiaCommuniCamState.Disconnected, peer.Status.State);
            // A retransmitted DISC must still be acknowledged.
            Assert.Equal("F981730160F9", Convert.ToHexString(peer.SendChannelControl(0x53)));
            Assert.Equal("F981730160F9", Convert.ToHexString(peer.SendChannelControl(0x3f)));
            Assert.Equal(0xa0, peer.ConnectObex());
            Assert.Contains("stored-images=\"1\"", Encoding.ASCII.GetString(peer.GetCameraInfo()));
            AssertEbmp(peer.GetThumbnail("0000001"), 101, 80);
        }
    }

    [Fact]
    public void ObexUnknownHandlesReturnNotFound()
    {
        using var peer = new CommuniCamProtocolTestPeer();

        Assert.Equal(0xc4, peer.Delete("9999999"));
    }

    static XDocument ParseXml(byte[] bytes) =>
        XDocument.Parse(Encoding.ASCII.GetString(bytes), LoadOptions.None);

    static void AssertEbmp(byte[] encoded, int width, int height)
    {
        Assert.Equal(14 + width * height + 1, encoded.Length);
        Assert.Equal(
            "45424D505F312E3005",
            Convert.ToHexString(encoded.AsSpan(0, 9)));
        Assert.Equal(
            width,
            BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(9, 2)));
        Assert.Equal(
            height,
            BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(11, 2)));
        Assert.Equal(8, encoded[13]);
        Assert.Equal(0, encoded[^1]);
    }

    static void AssertJpeg(byte[] encoded, int width, int height)
    {
        Assert.True(encoded.Length > 600);
        Assert.Equal(0xff, encoded[0]);
        Assert.Equal(0xd8, encoded[1]);
        Assert.Equal("JFIF", Encoding.ASCII.GetString(encoded, 6, 4));
        Assert.Equal(0xff, encoded[^2]);
        Assert.Equal(0xd9, encoded[^1]);
        int startOfFrame = FindMarker(encoded, 0xc0);
        Assert.True(startOfFrame > 0);
        Assert.Equal(
            height,
            BinaryPrimitives.ReadUInt16BigEndian(
                encoded.AsSpan(startOfFrame + 5, 2)));
        Assert.Equal(
            width,
            BinaryPrimitives.ReadUInt16BigEndian(
                encoded.AsSpan(startOfFrame + 7, 2)));
    }

    static int FindMarker(ReadOnlySpan<byte> encoded, byte marker)
    {
        for (var offset = 2; offset + 1 < encoded.Length; offset++)
        {
            if (encoded[offset] == 0xff && encoded[offset + 1] == marker)
            {
                return offset;
            }
        }
        return -1;
    }
}
