// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class S4595DisplayTests
{
    [Fact]
    public void OnlyCommonPanelWireAddressIsAcknowledged()
    {
        var display = new S4595Display();

        Assert.True(display.WriteTransaction(0x72, [0x00, 0x00]));
        Assert.False(display.WriteTransaction(0x70, [0x00, 0x00]));
        Assert.False(display.WriteTransaction(0x74, [0x00, 0x00]));
        Assert.Equal(1, display.TransactionCount);
    }

    [Fact]
    public void RegisterPairsFromFirmwareConfigureVisibleWindowAndCursor()
    {
        var display = new S4595Display();

        Assert.True(display.WriteTransaction(0x72,
            [0x07, 0x1b, 0x08, 0x7f, 0x09, 0x00, 0x0a, 0x4f, 0x04, 0x1d, 0x05, 0x03]));

        Assert.Equal(0x1b, display.ReadRegister(0x07));
        Assert.Equal(0x7f, display.ReadRegister(0x08));
        Assert.Equal(0x1d, display.CursorColumn);
        Assert.Equal(0x03, display.CursorRow);
    }

    [Fact]
    public void ScanRowCommandStreamsPixelsFromCurrentCursor()
    {
        var display = new S4595Display();
        display.WriteTransaction(0x72, [0x04, 0x1c, 0x05, 0x02]);

        Assert.True(display.WriteTransaction(0x72, [0x8c, 0xe0, 0x1c, 0x03]));

        Assert.Equal(0xe0, display.GetPixel(1, 2));
        Assert.Equal(0x1c, display.GetPixel(2, 2));
        Assert.Equal(0x03, display.GetPixel(3, 2));
        Assert.Equal(3, display.PixelWriteCount);
        Assert.Equal(0x1f, display.CursorColumn);
        Assert.Equal(0x02, display.CursorRow);
    }

    [Fact]
    public void PixelStreamWrapsAtFirmwareWindowBoundaries()
    {
        var display = new S4595Display();
        display.WriteTransaction(0x72, [0x04, 0x7e, 0x05, 0x4f]);

        display.WriteTransaction(0x72, [0x8c, 0x11, 0x22, 0x33]);

        Assert.Equal(0x11, display.GetPixel(99, 79));
        Assert.Equal(0x22, display.GetPixel(100, 79));
        Assert.Equal(0x33, display.GetPixel(0, 0));
        Assert.Equal(0x1c, display.CursorColumn);
        Assert.Equal(0x00, display.CursorRow);
    }

    [Fact]
    public void PartialScanHidesRetainedRamAndMovesWithoutAnotherPixelWrite()
    {
        var display = new S4595Display();
        display.WriteTransaction(0x72, [0x04, 0x1b, 0x05, 0x04]);
        display.WriteTransaction(0x72, [0x8c, 0xe0]);
        display.WriteTransaction(0x72, [0x04, 0x1b, 0x05, 0x1c]);
        display.WriteTransaction(0x72, [0x8c, 0x1c]);
        display.WriteTransaction(0x72, [0x11, 0x17, 0x01, 0x08, 0x16, 0x04]);
        Assert.Equal(0xe0, display.Framebuffer[4 * 101]);
        Assert.Equal(0, display.Framebuffer[28 * 101]);
        display.WriteTransaction(0x72, [0x16, 0x1c]);
        Assert.Equal(0, display.Framebuffer[4 * 101]);
        Assert.Equal(0x1c, display.Framebuffer[28 * 101]);
        display.WriteTransaction(0x72, [0x01, 0x00]);
        Assert.Equal(0xe0, display.Framebuffer[4 * 101]);
        Assert.Equal(0x1c, display.Framebuffer[28 * 101]);
    }

    [Fact]
    public void SmallerWriteWindowUsesPhysicalPanelCoordinates()
    {
        var display = new S4595Display();
        display.WriteTransaction(0x72,
            [0x07, 0x20, 0x08, 0x21, 0x09, 0x04, 0x0a, 0x05, 0x04, 0x20, 0x05, 0x04]);
        display.WriteTransaction(0x72, [0x8c, 1, 2, 3]);
        Assert.Equal(0, display.GetPixel(0, 0));
        Assert.Equal(1, display.GetPixel(5, 4));
        Assert.Equal(2, display.GetPixel(6, 4));
        Assert.Equal(3, display.GetPixel(5, 5));
    }

    [Theory]
    [InlineData(0xe0, 255, 0, 0)]
    [InlineData(0x1c, 0, 255, 0)]
    [InlineData(0x03, 0, 0, 255)]
    [InlineData(0xff, 255, 255, 255)]
    public void FirmwareColorLayoutExpandsAsRgb332(byte pixel, byte red, byte green, byte blue)
    {
        Assert.Equal((red, green, blue), S4595Display.ExpandRgb332(pixel));
    }
}
