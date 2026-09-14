// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicCommandPortTests
{
    [Fact]
    public void StatusReportsCommandFifoReadyWithoutChangingOtherBits()
    {
        var cpu = new Cpu(new byte[2], 0x200);
        cpu.Data[AsicCommandPort.StatusAddress] = 0x81;

        _ = new AsicCommandPort(cpu);

        Assert.Equal(0x91, cpu.ReadData(AsicCommandPort.StatusAddress));
        Assert.Equal(0x81, cpu.Data[AsicCommandPort.StatusAddress]);
    }

    [Fact]
    public void CapturesEveryCommandByteWithoutConsumingTheWrite()
    {
        var cpu = new Cpu(new byte[2], 0x200);
        var commands = new List<byte>();
        var port = new AsicCommandPort(cpu);
        port.ByteWritten += commands.Add;

        cpu.WriteData(AsicCommandPort.DataAddress, 0x12);
        cpu.WriteData(AsicCommandPort.DataAddress, 0x8c);

        Assert.Equal(2, port.WrittenByteCount);
        Assert.Equal(new byte[] { 0x12, 0x8c }, commands);
        Assert.Equal(0x8c, cpu.Data[AsicCommandPort.DataAddress]);
    }

    [Fact]
    public void PatCopyTilesFirmwareSelectedPatternIntoRgb332Surface()
    {
        const int patternAddress = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        for (var index = 0; index < 64; index++)
        {
            cpu.Data[patternAddress + index] = (byte)(index + 1);
        }
        Array.Fill(cpu.Data, (byte)0xee, surfaceAddress, 12);

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x04, 0x06,
            0x00, 0x04, 0x00, 0x03);
        WritePacket(cpu, 0x12, 0x02, 0x00);
        WritePacket(cpu, 0x11, 0x00, 0x00, 0x20, 0xf0, 0x00, 0x00, 0x04, 0x03);

        Assert.Equal(new byte[]
        {
            1, 2, 3, 4,
            9, 10, 11, 12,
            17, 18, 19, 20,
        }, cpu.Data[surfaceAddress..(surfaceAddress + 12)]);
        Assert.Equal(3, port.CompletedPacketCount);
        Assert.Equal(1, port.RasterOperationCount);
        Assert.Equal(12, port.PixelWriteCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    [Fact]
    public void UnsupportedSurfaceFormatFailsClosedWithoutWritingRam()
    {
        const int patternAddress = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        cpu.Data[patternAddress] = 0x55;
        cpu.Data[surfaceAddress] = 0xaa;

        WritePacket(cpu, 0x10, 0x02, 0x02, 0x09, 0x04, 0x00, 0x01, 0x06,
            0x00, 0x01, 0x00, 0x01);
        WritePacket(cpu, 0x12, 0x02, 0x00);
        WritePacket(cpu, 0x11, 0x00, 0x00, 0x20, 0xf0, 0x00, 0x00, 0x01, 0x01);

        Assert.Equal(0xaa, cpu.Data[surfaceAddress]);
        Assert.Equal(0, port.RasterOperationCount);
        Assert.Equal(2, port.RejectedPacketCount);
    }

    [Fact]
    public void CurrentSurfaceRectangleUpdateClipsFollowingPatternBlt()
    {
        const int patternAddress = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        Array.Fill(cpu.Data, (byte)0x5a, patternAddress, 64);
        Array.Fill(cpu.Data, (byte)0xee, surfaceAddress, 12);

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x04, 0x06,
            0x00, 0x04, 0x00, 0x03);
        WritePacket(cpu, 0x06, 0x01, 0x03, 0x01, 0x03);
        WritePacket(cpu, 0x12, 0x02, 0x00);
        WritePacket(cpu, 0x11, 0x00, 0x00, 0x20, 0xf0, 0x00, 0x00, 0x04, 0x03);

        Assert.Equal(new byte[]
        {
            0xee, 0xee, 0xee, 0xee,
            0xee, 0x5a, 0x5a, 0xee,
            0xee, 0x5a, 0x5a, 0xee,
        }, cpu.Data[surfaceAddress..(surfaceAddress + 12)]);
        Assert.Equal(4, port.CompletedPacketCount);
        Assert.Equal(1, port.RasterOperationCount);
        Assert.Equal(4, port.PixelWriteCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    [Fact]
    public void PixelReadbackUsesFirmwareYThenXCoordinates()
    {
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        new byte[]
        {
            0x10, 0x11, 0x12, 0x13,
            0x20, 0x21, 0x22, 0x23,
            0x30, 0x31, 0x32, 0x33,
        }.CopyTo(cpu.Data, surfaceAddress);

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x04, 0x06,
            0x00, 0x04, 0x00, 0x03);
        WritePacket(cpu, 0x05, 0x02, 0x01);

        Assert.Equal(0x31, cpu.ReadData(AsicCommandPort.ResponseAddress));
        Assert.Equal(2, port.CompletedPacketCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    [Fact]
    public void PixelReadbackPacketDoesNotConsumeFollowingPenCommand()
    {
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        cpu.Data[surfaceAddress] = 0xa5;

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x01, 0x06,
            0x00, 0x01, 0x00, 0x01);
        WritePacket(cpu, 0x05, 0x00, 0x00);
        WritePacket(cpu, 0x0b, 0x5a);
        WritePacket(cpu, 0x4c, 0x00, 0x00);

        Assert.Equal(0xa5, cpu.ReadData(AsicCommandPort.ResponseAddress));
        Assert.Equal(0x5a, cpu.Data[surfaceAddress]);
        Assert.Equal(4, port.CompletedPacketCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    [Fact]
    public void BltUsesDestinationOriginAndExtentRatherThanRightBottomCoordinates()
    {
        const int patternAddress = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        Array.Fill(cpu.Data, (byte)0x5a, patternAddress, 64);
        Array.Fill(cpu.Data, (byte)0xee, surfaceAddress, 30);

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x06, 0x06,
            0x00, 0x06, 0x00, 0x05);
        WritePacket(cpu, 0x12, 0x02, 0x00);
        WritePacket(cpu, 0x11, 0x00, 0x00, 0x20, 0xf0, 0x02, 0x01, 0x03, 0x02);

        Assert.Equal(new byte[]
        {
            0xee, 0xee, 0xee, 0xee, 0xee, 0xee,
            0xee, 0xee, 0x5a, 0x5a, 0x5a, 0xee,
            0xee, 0xee, 0x5a, 0x5a, 0x5a, 0xee,
            0xee, 0xee, 0xee, 0xee, 0xee, 0xee,
            0xee, 0xee, 0xee, 0xee, 0xee, 0xee,
        }, cpu.Data[surfaceAddress..(surfaceAddress + 30)]);
        Assert.Equal(6, port.PixelWriteCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    [Fact]
    public void SourceCopyReadsSelectedRgb332Surface()
    {
        const int sourceAddress = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int destinationAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }
            .CopyTo(cpu.Data, sourceAddress);
        Array.Fill(cpu.Data, (byte)0xee, destinationAddress, 24);

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x06, 0x06,
            0x00, 0x06, 0x00, 0x04);
        WritePacket(cpu, 0x0a, 0x02, 0x00, 0x04);
        WritePacket(cpu, 0x11, 0x00, 0x00, 0x20, 0xcc, 0x01, 0x01, 0x04, 0x02);

        Assert.Equal(new byte[]
        {
            0xee, 0xee, 0xee, 0xee, 0xee, 0xee,
            0xee, 1, 2, 3, 4, 0xee,
            0xee, 5, 6, 7, 8, 0xee,
            0xee, 0xee, 0xee, 0xee, 0xee, 0xee,
        }, cpu.Data[destinationAddress..(destinationAddress + 24)]);
        Assert.Equal(8, port.PixelWriteCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    [Fact]
    public void CopyPenAndMonochromeSetBitsUseFirmwareCoordinates()
    {
        const int bitmapAddress = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int surfaceAddress = AsicCommandPort.GraphicsRamBase + 0x0400;
        var cpu = new Cpu(new byte[2], 0x110000);
        var port = new AsicCommandPort(cpu);
        new byte[] { 0x01, 0x02, 0x01, 0x02, 0x01 }
            .CopyTo(cpu.Data, bitmapAddress);
        Array.Fill(cpu.Data, (byte)0xff, surfaceAddress, 24);

        WritePacket(cpu, 0x10, 0x03, 0x02, 0x09, 0x04, 0x00, 0x06, 0x06,
            0x00, 0x06, 0x00, 0x04);
        WritePacket(cpu, 0x0b, 0x49);
        WritePacket(cpu, 0x4c, 0x02, 0x03);
        WritePacket(cpu, 0x0b, 0x00);
        WritePacket(cpu, 0x0f, 0x02, 0x00, 0x05, 0x02);
        WritePacket(cpu, 0x80, 0x01, 0x01);

        Assert.Equal(new byte[]
        {
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            0xff, 0x00, 0xff, 0x00, 0xff, 0x00,
            0xff, 0xff, 0x00, 0x49, 0x00, 0xff,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        }, cpu.Data[surfaceAddress..(surfaceAddress + 24)]);
        Assert.Equal(6, port.PixelWriteCount);
        Assert.Equal(0, port.RejectedPacketCount);
    }

    static void WritePacket(Cpu cpu, params byte[] bytes)
    {
        foreach (var value in bytes)
        {
            cpu.WriteData(AsicCommandPort.DataAddress, value);
        }
    }
}
