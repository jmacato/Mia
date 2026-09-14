// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class GdfsOverlayTests
{
    [Fact]
    public void RoundTripStoresOnlyChangedEraseBlocks()
    {
        var pristine = ErasedRaw();
        pristine[0x1234] = 0x12;
        var current = pristine.ToArray();
        current[0x1234] = 0xff;
        current[0x21042] = 0x34;

        var overlay = GdfsOverlay.Create(pristine, current);
        var restored = GdfsOverlay.Apply(pristine, overlay.Image);

        Assert.Equal(2, overlay.ChangedBlockCount);
        Assert.Equal(2, restored.ChangedBlockCount);
        Assert.Equal(current, restored.RawImage);
        Assert.Equal(88 + 2 * (4 + GdfsOverlay.BlockLength), overlay.Image.Length);
    }

    [Fact]
    public void EmptyOverlayRetainsPristineImage()
    {
        var pristine = ErasedRaw();

        var overlay = GdfsOverlay.Create(pristine, pristine);
        var restored = GdfsOverlay.Apply(pristine, overlay.Image);

        Assert.Equal(0, overlay.ChangedBlockCount);
        Assert.Equal(pristine, restored.RawImage);
    }

    [Fact]
    public void RejectsOverlayForDifferentPristineImage()
    {
        var pristine = ErasedRaw();
        var current = pristine.ToArray();
        current[0] = 0x12;
        var overlay = GdfsOverlay.Create(pristine, current);
        pristine[1] = 0x34;

        var error = Assert.Throws<InvalidDataException>(
            () => GdfsOverlay.Apply(pristine, overlay.Image));

        Assert.Contains("different pristine", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCorruptPayload()
    {
        var pristine = ErasedRaw();
        var current = pristine.ToArray();
        current[0] = 0x12;
        var overlay = GdfsOverlay.Create(pristine, current).Image;
        overlay[^1] ^= 1;

        var error = Assert.Throws<InvalidDataException>(
            () => GdfsOverlay.Apply(pristine, overlay));

        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateBlockRecordsEvenWithValidPayloadChecksum()
    {
        var pristine = ErasedRaw();
        var current = pristine.ToArray();
        current[0] = 0x12;
        current[GdfsOverlay.BlockLength] = 0x34;
        var overlay = GdfsOverlay.Create(pristine, current).Image;
        var secondRecord = 88 + 4 + GdfsOverlay.BlockLength;
        BinaryPrimitives.WriteInt32LittleEndian(overlay.AsSpan(secondRecord), 0);
        System.Security.Cryptography.SHA256.HashData(
            overlay.AsSpan(88),
            overlay.AsSpan(56, 32));

        var error = Assert.Throws<InvalidDataException>(
            () => GdfsOverlay.Apply(pristine, overlay));

        Assert.Contains("duplicate", error.Message, StringComparison.Ordinal);
    }

    static byte[] ErasedRaw()
    {
        var raw = new byte[GdfsImage.RawLength];
        Array.Fill(raw, (byte)0xff);
        return raw;
    }
}
