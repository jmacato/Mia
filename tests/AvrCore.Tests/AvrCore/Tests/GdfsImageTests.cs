// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class GdfsImageTests
{
    [Fact]
    public void DecodeRawPreservesErasedBytesOutsidePartialSRecordImage()
    {
        var image = CreateSbn(0x600000, [1, 2, 3, 4]);

        var raw = GdfsImage.DecodeRaw(image);

        Assert.Equal(GdfsImage.RawLength, raw.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, raw.AsSpan(0x80000, 4).ToArray());
        Assert.All(raw.AsSpan(0, 0x80000).ToArray(), value => Assert.Equal(0xff, value));
    }

    [Fact]
    public void LoadsBigPayloadAtDeclaredFlashAddress()
    {
        var payload = Enumerable.Repeat((byte)0xff, GdfsImage.UfsXorPeriod * 4).ToArray();
        payload[0] = 0x12;
        payload[1] = 0x34;
        payload[2] = 0x56;
        var image = CreateBig(0x20, payload);
        var program = new byte[0x20 + payload.Length];

        var loaded = GdfsImage.LoadBig(image, program);

        Assert.Equal((0x20, payload.Length), loaded);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, program[0x20..0x23]);
    }

    [Fact]
    public void RejectsInconsistentOuterLength()
    {
        var image = CreateBig(0x20, new byte[GdfsImage.UfsXorPeriod * 2]);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4), image.Length - 7);

        Assert.Throws<InvalidDataException>(() => GdfsImage.LoadBig(image, new byte[0x40]));
    }

    [Fact]
    public void RejectsPayloadOutsideProgramFlash()
    {
        var image = CreateBig(0x3f, new byte[GdfsImage.UfsXorPeriod * 2]);

        Assert.Throws<InvalidDataException>(() => GdfsImage.LoadBig(image, new byte[0x40]));
    }

    [Fact]
    public void LoadsBinarySRecordPayloadAtDeclaredFlashAddress()
    {
        var image = CreateSbn(0x20, [0x12, 0x34, 0x56]);
        var program = new byte[0x40];

        var loaded = GdfsImage.Load(image, program);

        Assert.Equal((0x20, 3), loaded);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, program[0x20..0x23]);
    }

    [Fact]
    public void LoadsRawFlashCaptureAtTheCompleteGdfsWindow()
    {
        var image = new byte[GdfsImage.RawLength];
        image[0] = 0x12;
        image[^1] = 0x34;
        var program = new byte[GdfsImage.RawAddress + GdfsImage.RawLength];

        var loaded = GdfsImage.Load(image, program);

        Assert.Equal((GdfsImage.RawAddress, GdfsImage.RawLength), loaded);
        Assert.Equal(0x12, program[GdfsImage.RawAddress]);
        Assert.Equal(0x34, program[^1]);
    }

    [Fact]
    public void RejectsBinarySRecordWithBadChecksum()
    {
        var image = CreateSbn(0x20, [0x12, 0x34]);
        image[^3] ^= 1;

        Assert.Throws<InvalidDataException>(() => GdfsImage.Load(image, new byte[0x40]));
    }

    static byte[] CreateBig(int address, byte[] payload)
    {
        var image = new byte[GdfsImage.HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(image, address);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4), payload.Length + 12);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(8), address);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(12), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 0x12345678);
        var key = Enumerable.Range(0, GdfsImage.UfsXorPeriod)
            .Select(index => (byte)(index * 73 + 19))
            .ToArray();
        for (var offset = 0; offset < payload.Length; offset++)
        {
            image[GdfsImage.HeaderLength + offset] = (byte)(payload[offset] ^ key[offset % key.Length]);
        }
        return image;
    }

    static byte[] CreateSbn(int address, byte[] payload)
    {
        const int addressLength = 3;
        var count = addressLength + payload.Length + 1;
        var image = new byte[4 + 3 + count + 2];
        "S003"u8.CopyTo(image);
        image[4] = (byte)'S';
        image[5] = (byte)'2';
        image[6] = (byte)count;
        image[7] = (byte)(address >> 16);
        image[8] = (byte)(address >> 8);
        image[9] = (byte)address;
        payload.CopyTo(image, 10);
        var sum = count;
        for (var index = 7; index < 10 + payload.Length; index++)
        {
            sum += image[index];
        }
        image[10 + payload.Length] = (byte)~sum;
        image[^2] = (byte)'S';
        image[^1] = (byte)'8';
        return image;
    }
}
