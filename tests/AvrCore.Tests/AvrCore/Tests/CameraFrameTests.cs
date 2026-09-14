// SPDX-License-Identifier: MIT

using Xunit;

namespace AvrCore.Tests;

public sealed class CameraFrameTests
{
    [Fact]
    public void SensorSizedBgraFramesDownsampleToCommuniCamMonitoringRgb332()
    {
        const int width = MiaCommuniCamImageProfile.SensorWidth;
        const int height = MiaCommuniCamImageProfile.SensorHeight;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x + y);
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)x;
                pixels[offset + 3] = 0xff;
            }
        }
        MiaCameraFrame frame = MiaCameraFrame.Create(
            width,
            height,
            width * 4,
            MiaCameraPixelFormat.Bgra32,
            pixels);

        byte[] encoded = MiaCommuniCamMonitoringImageEncoder.Encode(frame);

        Assert.Equal(4_815, encoded.Length);
        Assert.Equal(
            "45424D505F312E30050050003C08",
            Convert.ToHexString(encoded.AsSpan(0, 14)));
        Assert.Equal(ToRgb332(sourceX: 0, sourceY: 0), encoded[14]);
        Assert.Equal(
            ToRgb332(sourceX: 80, sourceY: 80),
            encoded[14 + 10 * 80 + 10]);
        Assert.Equal(
            ToRgb332(sourceX: 632, sourceY: 472),
            encoded[^2]);
        Assert.Equal(0, encoded[^1]);
    }

    [Fact]
    public void FrameLayoutRejectsShortBackingMemory()
    {
        Assert.Throws<ArgumentException>(() => MiaCameraFrame.Create(
            80,
            60,
            80 * 4,
            MiaCameraPixelFormat.Bgra32,
            new byte[80 * 60 * 4 - 1]));
    }

    static byte ToRgb332(int sourceX, int sourceY)
    {
        byte blue = (byte)(sourceX + sourceY);
        byte green = (byte)sourceY;
        byte red = (byte)sourceX;
        return (byte)((red & 0xe0) | ((green >> 3) & 0x1c) | (blue >> 6));
    }
}
