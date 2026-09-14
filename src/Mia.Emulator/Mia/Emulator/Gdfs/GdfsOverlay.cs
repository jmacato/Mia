// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Mia.Emulator.Gdfs;

/// <summary>
/// Encodes persistent copy-on-write state separately from a pristine GDFS
/// service image. Complete changed NOR erase blocks are recorded so both
/// programmed bits and firmware-issued erases survive a later boot.
/// </summary>
internal static class GdfsOverlay
{
    public const int BlockLength = 0x10000;

    const int Version = 1;
    const int HeaderLength = 88;
    const int RecordHeaderLength = 4;
    static ReadOnlySpan<byte> Magic => "MIAGDFO"u8;

    public static GdfsOverlayFile Create(
        ReadOnlySpan<byte> pristineRaw,
        ReadOnlySpan<byte> currentRaw)
    {
        ValidateRaw(pristineRaw, nameof(pristineRaw));
        ValidateRaw(currentRaw, nameof(currentRaw));

        var changedBlocks = new List<int>();
        for (var offset = 0; offset < GdfsImage.RawLength; offset += BlockLength)
        {
            if (!pristineRaw.Slice(offset, BlockLength)
                    .SequenceEqual(currentRaw.Slice(offset, BlockLength)))
            {
                changedBlocks.Add(offset / BlockLength);
            }
        }

        var recordLength = RecordHeaderLength + BlockLength;
        var image = new byte[checked(HeaderLength + changedBlocks.Count * recordLength)];
        Magic.CopyTo(image);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(12), GdfsImage.RawLength);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(16), BlockLength);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(20), changedBlocks.Count);
        SHA256.HashData(pristineRaw, image.AsSpan(24, SHA256.HashSizeInBytes));

        var cursor = HeaderLength;
        foreach (var block in changedBlocks)
        {
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(cursor), block);
            currentRaw.Slice(block * BlockLength, BlockLength)
                .CopyTo(image.AsSpan(cursor + RecordHeaderLength));
            cursor += recordLength;
        }
        SHA256.HashData(
            image.AsSpan(HeaderLength),
            image.AsSpan(56, SHA256.HashSizeInBytes));
        return new GdfsOverlayFile(image, changedBlocks.Count);
    }

    public static GdfsOverlayState Apply(
        ReadOnlySpan<byte> pristineRaw,
        ReadOnlySpan<byte> overlay)
    {
        ValidateRaw(pristineRaw, nameof(pristineRaw));
        ValidateHeader(overlay);

        int recordCount = ReadAndValidateGeometry(overlay);
        ValidateOverlayLength(overlay.Length, recordCount);
        ValidateBaseHash(pristineRaw, overlay);
        ValidatePayloadHash(overlay);
        byte[] raw = ApplyRecords(pristineRaw, overlay, recordCount);
        return new GdfsOverlayState(raw, recordCount);
    }

    static void ValidateHeader(ReadOnlySpan<byte> overlay)
    {
        if (overlay.Length < HeaderLength ||
            !overlay[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("GDFS overlay has an invalid header.");
        }
    }

    static int ReadAndValidateGeometry(ReadOnlySpan<byte> overlay)
    {
        var version = BinaryPrimitives.ReadInt32LittleEndian(overlay[8..]);
        var rawLength = BinaryPrimitives.ReadInt32LittleEndian(overlay[12..]);
        var blockLength = BinaryPrimitives.ReadInt32LittleEndian(overlay[16..]);
        var recordCount = BinaryPrimitives.ReadInt32LittleEndian(overlay[20..]);
        if (version != Version || rawLength != GdfsImage.RawLength ||
            blockLength != BlockLength || recordCount < 0 ||
            recordCount > GdfsImage.RawLength / BlockLength)
        {
            throw new InvalidDataException("GDFS overlay geometry or version is unsupported.");
        }
        return recordCount;
    }

    static void ValidateOverlayLength(int overlayLength, int recordCount)
    {
        int expectedLength = checked(
            HeaderLength + recordCount * (RecordHeaderLength + BlockLength));
        if (overlayLength != expectedLength)
        {
            throw new InvalidDataException("GDFS overlay length is inconsistent with its record count.");
        }
    }

    static void ValidateBaseHash(
        ReadOnlySpan<byte> pristineRaw,
        ReadOnlySpan<byte> overlay)
    {
        Span<byte> baseHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(pristineRaw, baseHash);
        if (!CryptographicOperations.FixedTimeEquals(baseHash, overlay.Slice(24, baseHash.Length)))
        {
            throw new InvalidDataException(
                "GDFS overlay belongs to a different pristine service image.");
        }
    }

    static void ValidatePayloadHash(ReadOnlySpan<byte> overlay)
    {
        Span<byte> payloadHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(overlay[HeaderLength..], payloadHash);
        if (!CryptographicOperations.FixedTimeEquals(
                payloadHash,
                overlay.Slice(56, payloadHash.Length)))
        {
            throw new InvalidDataException("GDFS overlay payload checksum is invalid.");
        }
    }

    static byte[] ApplyRecords(
        ReadOnlySpan<byte> pristineRaw,
        ReadOnlySpan<byte> overlay,
        int recordCount)
    {
        byte[] raw = pristineRaw.ToArray();
        Span<bool> seenBlocks = stackalloc bool[GdfsImage.RawLength / BlockLength];
        var cursor = HeaderLength;
        for (var record = 0; record < recordCount; record++)
        {
            var block = BinaryPrimitives.ReadInt32LittleEndian(overlay[cursor..]);
            if ((uint)block >= (uint)seenBlocks.Length || seenBlocks[block])
            {
                throw new InvalidDataException(
                    "GDFS overlay contains a duplicate or out-of-range erase block.");
            }
            seenBlocks[block] = true;
            overlay.Slice(cursor + RecordHeaderLength, BlockLength)
                .CopyTo(raw.AsSpan(block * BlockLength, BlockLength));
            cursor += RecordHeaderLength + BlockLength;
        }
        return raw;
    }

    static void ValidateRaw(ReadOnlySpan<byte> raw, string parameterName)
    {
        if (raw.Length != GdfsImage.RawLength)
        {
            throw new ArgumentException(
                $"Raw GDFS state must be exactly 0x{GdfsImage.RawLength:x} bytes.",
                parameterName);
        }
    }
}
