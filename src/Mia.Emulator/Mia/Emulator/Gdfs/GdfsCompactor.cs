// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Mia.Emulator.Gdfs;

/// <summary>
/// Removes superseded records from active physical GDFS blocks while
/// preserving the record that the firmware's newest-first catalogue scan
/// observes for every key.
/// </summary>
internal static class GdfsCompactor
{
    public const int BlockLength = 0x10000;
    public const int BlockHeaderLength = 4;
    public const int EntryLength = 9;

    const byte ActiveBlockState = 0x1f;

    public static GdfsCompactionResult Compact(ReadOnlySpan<byte> source)
    {
        if (source.Length != GdfsImage.RawLength)
        {
            throw new InvalidDataException(
                $"Raw GDFS image must be exactly 0x{GdfsImage.RawLength:x} bytes.");
        }

        var output = source.ToArray();
        var blocks = new List<GdfsBlockCompaction>();
        for (var physicalBlock = 0;
             physicalBlock < source.Length / BlockLength;
             physicalBlock++)
        {
            var sourceBlock = source.Slice(physicalBlock * BlockLength, BlockLength);
            var destinationBlock = output.AsSpan(physicalBlock * BlockLength, BlockLength);
            GdfsBlockCompaction? result = CompactBlock(
                sourceBlock,
                destinationBlock,
                physicalBlock);
            if (result is not null)
            {
                blocks.Add(result);
            }
        }

        return new GdfsCompactionResult(output, blocks);
    }

    static GdfsBlockCompaction? CompactBlock(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int physicalBlock)
    {
        if (source[0] != ActiveBlockState)
        {
            return null;
        }

        List<GdfsCompactorEntry> entries = ReadEntries(source, physicalBlock);
        if (entries.Count == 0)
        {
            return null;
        }

        List<GdfsCompactorEntry> currentEntries = SelectCurrentEntries(entries);
        WriteCurrentEntries(source, destination, physicalBlock, currentEntries);
        VerifyEquivalent(source, destination, physicalBlock);
        return new GdfsBlockCompaction(
            physicalBlock,
            source[1],
            entries.Count,
            currentEntries.Count,
            CountProgrammedBytes(source) - CountProgrammedBytes(destination));
    }

    static List<GdfsCompactorEntry> SelectCurrentEntries(
        List<GdfsCompactorEntry> entries)
    {
        var seenKeys = new HashSet<ushort>();
        var currentEntries = new List<GdfsCompactorEntry>();
        foreach (GdfsCompactorEntry entry in entries)
        {
            if (seenKeys.Add(entry.Key))
            {
                currentEntries.Add(entry);
            }
        }
        return currentEntries;
    }

    static void WriteCurrentEntries(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int physicalBlock,
        List<GdfsCompactorEntry> entries)
    {
        destination.Fill(0xff);
        source[..BlockHeaderLength].CopyTo(destination);

        var headerCursor = BlockLength - entries.Count * EntryLength;
        foreach (GdfsCompactorEntry entry in entries)
        {
            if (entry.DataOffset + entry.Length > headerCursor)
            {
                throw InvalidBlock(
                    physicalBlock,
                    $"current key 0x{entry.Key:x4} data overlaps the compacted catalogue");
            }

            source.Slice(entry.DataOffset, entry.Length)
                .CopyTo(destination[entry.DataOffset..]);
            WriteEntry(destination[headerCursor..], entry);
            headerCursor += EntryLength;
        }
    }

    static List<GdfsCompactorEntry> ReadEntries(ReadOnlySpan<byte> block, int physicalBlock)
    {
        var cursor = BlockLength;
        while (cursor >= EntryLength && IsEntryState(block[cursor - EntryLength]))
        {
            cursor -= EntryLength;
        }

        var entries = new List<GdfsCompactorEntry>((BlockLength - cursor) / EntryLength);
        for (var offset = cursor; offset < BlockLength; offset += EntryLength)
        {
            var entry = new GdfsCompactorEntry(
                block[offset],
                BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 1)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 3)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 5)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 7)..]));
            var dataEnd = (int)entry.DataOffset + entry.Length;
            if (entry.DataOffset < BlockHeaderLength || dataEnd > cursor)
            {
                throw InvalidBlock(
                    physicalBlock,
                    $"key 0x{entry.Key:x4} has data range " +
                    $"0x{entry.DataOffset:x4}..0x{dataEnd:x4} outside its data area");
            }
            if (Checksum(block.Slice(entry.DataOffset, entry.Length)) != entry.Checksum)
            {
                throw InvalidBlock(
                    physicalBlock,
                    $"key 0x{entry.Key:x4} has an invalid payload checksum");
            }
            entries.Add(entry);
        }
        return entries;
    }

    static void VerifyEquivalent(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> compacted,
        int physicalBlock)
    {
        if (!source[..BlockHeaderLength].SequenceEqual(compacted[..BlockHeaderLength]))
        {
            throw InvalidBlock(physicalBlock, "physical block header changed");
        }

        var sourceRecords = CurrentRecords(source, physicalBlock);
        var compactedRecords = CurrentRecords(compacted, physicalBlock);
        if (sourceRecords.Count != compactedRecords.Count)
        {
            throw InvalidBlock(physicalBlock, "current key count changed");
        }

        foreach (var (key, sourceEntry) in sourceRecords)
        {
            if (!compactedRecords.TryGetValue(key, out var compactedEntry) ||
                sourceEntry != compactedEntry ||
                !source.Slice(sourceEntry.DataOffset, sourceEntry.Length)
                    .SequenceEqual(compacted.Slice(compactedEntry.DataOffset, compactedEntry.Length)))
            {
                throw InvalidBlock(
                    physicalBlock,
                    $"firmware-visible record 0x{key:x4} changed");
            }
        }
    }

    static Dictionary<ushort, GdfsCompactorEntry> CurrentRecords(
        ReadOnlySpan<byte> block,
        int physicalBlock)
    {
        var records = new Dictionary<ushort, GdfsCompactorEntry>();
        foreach (var entry in ReadEntries(block, physicalBlock))
        {
            records.TryAdd(entry.Key, entry);
        }
        return records;
    }

    static void WriteEntry(Span<byte> destination, GdfsCompactorEntry entry)
    {
        destination[0] = entry.State;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], entry.Key);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..], entry.DataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[5..], entry.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[7..], entry.Checksum);
    }

    static bool IsEntryState(byte value) => value is 0x0f or 0x1f or 0x3f;

    static ushort Checksum(ReadOnlySpan<byte> payload)
    {
        ushort checksum = 0;
        foreach (var value in payload)
        {
            checksum += value;
        }
        return checksum;
    }

    static int CountProgrammedBytes(ReadOnlySpan<byte> block)
    {
        var count = 0;
        foreach (var value in block)
        {
            if (value != 0xff)
            {
                count++;
            }
        }
        return count;
    }

    static InvalidDataException InvalidBlock(int physicalBlock, string message) =>
        new($"GDFS physical block 0x{physicalBlock:x2}: {message}.");
}
