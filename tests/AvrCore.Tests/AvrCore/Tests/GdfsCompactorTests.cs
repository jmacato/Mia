// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class GdfsCompactorTests
{
    [Fact]
    public void KeepsNewestRecordAndDeletionTombstoneAtTheirOriginalOffsets()
    {
        var image = ErasedImage();
        var block = image.AsSpan(3 * GdfsCompactor.BlockLength, GdfsCompactor.BlockLength);
        new byte[] { 0x1f, 0x02, 0x07, 0x00 }.CopyTo(block);
        WritePayload(block, 0x20, [0x10, 0x20]);
        WritePayload(block, 0x30, [0x30, 0x40, 0x50]);
        WritePayload(block, 0x40, [0x60]);
        WriteEntry(block, new(
            HeaderOffset: block.Length - 27,
            State: 0x0f,
            Key: 0x2222,
            DataOffset: 0x40,
            Length: 1));
        WriteEntry(block, new(
            HeaderOffset: block.Length - 18,
            State: 0x3f,
            Key: 0x1111,
            DataOffset: 0x30,
            Length: 3));
        WriteEntry(block, new(
            HeaderOffset: block.Length - 9,
            State: 0x3f,
            Key: 0x1111,
            DataOffset: 0x20,
            Length: 2));

        var result = GdfsCompactor.Compact(image);
        var compacted = result.Image.AsSpan(
            3 * GdfsCompactor.BlockLength,
            GdfsCompactor.BlockLength);

        Assert.Equal(new byte[] { 0x1f, 0x02, 0x07, 0x00 }, compacted[..4].ToArray());
        Assert.Equal(0x0f, compacted[^18]);
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16LittleEndian(compacted[^17..]));
        Assert.Equal(0x3f, compacted[^9]);
        Assert.Equal(0x1111, BinaryPrimitives.ReadUInt16LittleEndian(compacted[^8..]));
        Assert.Equal(new byte[] { 0x30, 0x40, 0x50 }, compacted[0x30..0x33].ToArray());
        Assert.Equal(new byte[] { 0x60 }, compacted[0x40..0x41].ToArray());
        Assert.All(compacted[0x20..0x22].ToArray(), value => Assert.Equal((byte)0xff, value));
        Assert.Single(result.Blocks);
        Assert.Equal(1, result.RemovedEntryCount);
    }

    [Fact]
    public void LeavesNonActiveBlocksUntouched()
    {
        var image = ErasedImage();
        var block = image.AsSpan(7 * GdfsCompactor.BlockLength, GdfsCompactor.BlockLength);
        block[0] = 0x07;
        block[1] = 0x55;
        block[^9] = 0x3f;
        block[^8] = 0x12;
        var expected = block.ToArray();

        var result = GdfsCompactor.Compact(image);

        Assert.Equal(
            expected,
            result.Image.AsSpan(
                7 * GdfsCompactor.BlockLength,
                GdfsCompactor.BlockLength).ToArray());
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public void RejectsBadCurrentPayloadChecksum()
    {
        var image = ErasedImage();
        var block = image.AsSpan(2 * GdfsCompactor.BlockLength, GdfsCompactor.BlockLength);
        new byte[] { 0x1f, 0x01, 0x03, 0x00 }.CopyTo(block);
        WritePayload(block, 0x10, [1, 2, 3]);
        WriteEntry(block, new(
            HeaderOffset: block.Length - 9,
            State: 0x3f,
            Key: 0x1234,
            DataOffset: 0x10,
            Length: 3));
        block[^2] ^= 1;

        var error = Assert.Throws<InvalidDataException>(() => GdfsCompactor.Compact(image));

        Assert.Contains("invalid payload checksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsWrongRawImageLength()
    {
        Assert.Throws<InvalidDataException>(() => GdfsCompactor.Compact(new byte[12]));
    }

    static byte[] ErasedImage()
    {
        var image = new byte[GdfsImage.RawLength];
        Array.Fill(image, (byte)0xff);
        return image;
    }

    static void WritePayload(Span<byte> block, int offset, byte[] payload) =>
        payload.CopyTo(block[offset..]);

    static void WriteEntry(Span<byte> block, GdfsEntry entry)
    {
        var destination = block[entry.HeaderOffset..];
        destination[0] = entry.State;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], entry.Key);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..], entry.DataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[5..], entry.Length);
        ushort checksum = 0;
        foreach (var value in block.Slice(entry.DataOffset, entry.Length))
        {
            checksum += value;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(destination[7..], checksum);
    }

}
