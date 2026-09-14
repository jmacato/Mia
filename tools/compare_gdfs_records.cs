#!/usr/bin/env dotnet

using System.Buffers.Binary;
using System.Security.Cryptography;

const int BlockLength = 0x10000;
const int EntryLength = 9;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/compare_gdfs_records.cs -- BEFORE.raw AFTER.raw");
    return 2;
}

var before = File.ReadAllBytes(args[0]);
var after = File.ReadAllBytes(args[1]);
if (before.Length != after.Length || before.Length % BlockLength != 0)
{
    throw new InvalidDataException("Inputs must be equal-sized whole-block raw GDFS images.");
}

for (var physicalBlock = 0; physicalBlock < before.Length / BlockLength; physicalBlock++)
{
    var beforeBlock = before.AsSpan(physicalBlock * BlockLength, BlockLength);
    var afterBlock = after.AsSpan(physicalBlock * BlockLength, BlockLength);
    if (beforeBlock.SequenceEqual(afterBlock))
    {
        continue;
    }

    Console.WriteLine(
        $"BLOCK physical=0x{physicalBlock:x2} " +
        $"before-header={Convert.ToHexString(beforeBlock[..4])} " +
        $"after-header={Convert.ToHexString(afterBlock[..4])}");
    var beforeRecords = ReadCurrentRecords(beforeBlock);
    var afterRecords = ReadCurrentRecords(afterBlock);
    foreach (var key in beforeRecords.Keys.Union(afterRecords.Keys).Order())
    {
        beforeRecords.TryGetValue(key, out var beforeRecord);
        afterRecords.TryGetValue(key, out var afterRecord);
        if (beforeRecord.Payload is not null &&
            afterRecord.Payload is not null &&
            beforeRecord.State == afterRecord.State &&
            beforeRecord.Payload.AsSpan().SequenceEqual(afterRecord.Payload))
        {
            continue;
        }

        Console.WriteLine(
            $"  key=0x{key:x4} before={Describe(beforeRecord)} " +
            $"after={Describe(afterRecord)}");
    }
}

return 0;

static Dictionary<ushort, Record> ReadCurrentRecords(ReadOnlySpan<byte> block)
{
    var cursor = block.Length;
    while (cursor >= EntryLength && IsEntryState(block[cursor - EntryLength]))
    {
        cursor -= EntryLength;
    }

    var records = new Dictionary<ushort, Record>();
    for (var offset = cursor; offset < block.Length; offset += EntryLength)
    {
        var state = block[offset];
        var key = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 1)..]);
        if (records.ContainsKey(key))
        {
            continue;
        }

        var dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 3)..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 5)..]);
        if (dataOffset < 4 || dataOffset + length > cursor)
        {
            throw new InvalidDataException($"Invalid record key 0x{key:x4}.");
        }
        records.Add(key, new Record(state, block.Slice(dataOffset, length).ToArray()));
    }
    return records;
}

static bool IsEntryState(byte value) => value is 0x0f or 0x1f or 0x3f;

static string Describe(Record record)
{
    if (record.Payload is null)
    {
        return "missing";
    }

    var previewLength = Math.Min(24, record.Payload.Length);
    return $"state=0x{record.State:x2},len={record.Payload.Length}," +
        $"sha={Convert.ToHexStringLower(SHA256.HashData(record.Payload))[..12]}," +
        $"data={Convert.ToHexString(record.Payload.AsSpan(0, previewLength))}";
}

readonly record struct Record(byte State, byte[]? Payload);
