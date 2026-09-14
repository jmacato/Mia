// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text.Json;

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceJsonReader
{
    public static AccessTraceSnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var input = File.OpenRead(path);
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return Read(input);
        }
        using var gzip = new GZipStream(
            input,
            CompressionMode.Decompress,
            leaveOpen: true);
        return Read(gzip);
    }

    public static AccessTraceSnapshot Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using JsonDocument document = JsonDocument.Parse(source);
        JsonElement root = document.RootElement;
        string schema = root.GetProperty("schema").GetString() ?? "";
        if (!string.Equals(
                schema,
                AccessTraceSnapshot.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported access-trace schema '{schema}'.");
        }
        return new(
            schema,
            ReadWindow(root.GetProperty("window")),
            ReadClock(root.GetProperty("clock")),
            ReadCapture(root.GetProperty("capture")),
            ReadStorage(root.GetProperty("storage")),
            ReadReconciliation(root.GetProperty("reconciliation")),
            ReadAddresses(root.GetProperty("addresses")));
    }

    static AccessTraceWindow ReadWindow(JsonElement element) => new(
        element.GetProperty("startCycle").GetInt64(),
        element.GetProperty("endCycleExclusive").GetInt64(),
        element.GetProperty("binCount").GetInt32());

    static AccessTraceClockInfo ReadClock(JsonElement element) => new(
        element.GetProperty("machineCycleDomain").GetString() ?? "",
        element.GetProperty("machineCyclesPerSecond").GetInt32(),
        element.GetProperty("avrCyclesPerSecond").GetInt32(),
        element.GetProperty("avrToMachineNumerator").GetInt32(),
        element.GetProperty("avrToMachineDenominator").GetInt32(),
        element.GetProperty("avrToMachineOffset").GetInt64());

    static AccessTraceCaptureInfo ReadCapture(JsonElement element) => new(
        element.GetProperty("stopped").GetBoolean(),
        GetNullableInt64(element.GetProperty("stoppedAtCycle")));

    static AccessTraceStorageMetrics ReadStorage(JsonElement element) => new(
        element.GetProperty("addressCount").GetInt32(),
        element.GetProperty("timeBinCellCount").GetInt32(),
        element.GetProperty("binValueCellCount").GetInt32(),
        element.GetProperty("valueHistogramCellCount").GetInt32(),
        element.GetProperty("transitionCellCount").GetInt32(),
        element.GetProperty("programCounterCellCount").GetInt32(),
        element.GetProperty("binValueOverflowCount").GetInt64(),
        element.GetProperty("valueHistogramOverflowCount").GetInt64(),
        element.GetProperty("transitionOverflowCount").GetInt64(),
        element.GetProperty("programCounterOverflowCount").GetInt64());

    static AccessTraceReconciliationSnapshot[] ReadReconciliation(
        JsonElement element) => element
        .EnumerateArray()
        .Select(item => new AccessTraceReconciliationSnapshot(
            ParseBus(item.GetProperty("bus").GetString()),
            ParseOperation(item.GetProperty("operation").GetString()),
            item.GetProperty("callbackCount").GetInt64(),
            item.GetProperty("aggregatedCount").GetInt64(),
            item.GetProperty("outsideWindowCount").GetInt64()))
        .ToArray();

    static AccessTraceAddressSnapshot[] ReadAddresses(JsonElement element) =>
        element
            .EnumerateArray()
            .Select(ReadAddress)
            .ToArray();

    static AccessTraceAddressSnapshot ReadAddress(JsonElement element) => new(
        new(
            ParseBus(element.GetProperty("bus").GetString()),
            element.GetProperty("address").GetUInt64(),
            element.GetProperty("size").GetInt32(),
            GetNullableByte(element.GetProperty("device")),
            GetNullableByte(element.GetProperty("register"))),
        element.GetProperty("totalCount").GetInt64(),
        element.GetProperty("readCount").GetInt64(),
        element.GetProperty("writeCount").GetInt64(),
        element.GetProperty("changeCount").GetInt64(),
        element.GetProperty("firstCycle").GetInt64(),
        element.GetProperty("lastCycle").GetInt64(),
        element.GetProperty("operations")
            .EnumerateArray()
            .Select(ReadOperation)
            .ToArray());

    static AccessTraceOperationSnapshot ReadOperation(JsonElement element) => new(
        ParseOperation(element.GetProperty("operation").GetString()),
        element.GetProperty("count").GetInt64(),
        element.GetProperty("changeCount").GetInt64(),
        element.GetProperty("consumedWriteCount").GetInt64(),
        element.GetProperty("firstCycle").GetInt64(),
        element.GetProperty("lastCycle").GetInt64(),
        GetNullableUInt64(element.GetProperty("initialHeldValue")),
        element.GetProperty("bitWidth").GetInt32(),
        element.GetProperty("valueOverflowCount").GetInt64(),
        element.GetProperty("transitionOverflowCount").GetInt64(),
        element.GetProperty("programCounterOverflowCount").GetInt64(),
        element.GetProperty("maskOverflowCount").GetInt64(),
        element.GetProperty("binValueOverflowCount").GetInt64(),
        ReadCounts(element.GetProperty("values")),
        ReadTransitions(element.GetProperty("transitions")),
        ReadBitEdges(element.GetProperty("bitEdges")),
        ReadProgramCounters(element.GetProperty("programCounters")),
        ReadCounts(element.GetProperty("masks")),
        ReadTimeBins(element.GetProperty("timeBins")));

    static AccessTraceCount[] ReadCounts(JsonElement element) => element
        .EnumerateArray()
        .Select(item => new AccessTraceCount(
            item[0].GetUInt64(),
            item[1].GetInt64()))
        .ToArray();

    static AccessTraceTransitionCount[] ReadTransitions(JsonElement element) =>
        element
            .EnumerateArray()
            .Select(item => new AccessTraceTransitionCount(
                item[0].GetUInt64(),
                item[1].GetUInt64(),
                item[2].GetInt64()))
            .ToArray();

    static AccessTraceBitEdgeCount[] ReadBitEdges(JsonElement element) => element
        .EnumerateArray()
        .Select(item => new AccessTraceBitEdgeCount(
            item[0].GetInt32(),
            item[1].GetInt64(),
            item[2].GetInt64()))
        .ToArray();

    static AccessTracePcCount[] ReadProgramCounters(JsonElement element) =>
        element
            .EnumerateArray()
            .Select(item => new AccessTracePcCount(
                item[0].GetUInt64(),
                item[1].GetInt64()))
            .ToArray();

    static AccessTraceTimeBinSnapshot[] ReadTimeBins(JsonElement element) =>
        element
            .EnumerateArray()
            .Select(item => new AccessTraceTimeBinSnapshot(
                item[0].GetInt32(),
                item[1].GetInt64(),
                item[2].GetInt64(),
                item[3].GetUInt64(),
                item[4].GetInt64(),
                item[5]
                    .EnumerateArray()
                    .Select(value => new AccessTraceBinValueCount(
                        value[0].GetUInt64(),
                        value[1].GetInt64()))
                    .ToArray()))
            .ToArray();

    static AccessTraceBus ParseBus(string? value) => value switch
    {
        "avr" => AccessTraceBus.Avr,
        "primary-i2c" => AccessTraceBus.PrimaryI2c,
        "arm-mmio" => AccessTraceBus.ArmMmio,
        _ => throw new InvalidDataException($"Unknown trace bus '{value}'."),
    };

    static AccessTraceOperation ParseOperation(string? value) => value switch
    {
        "read" => AccessTraceOperation.Read,
        "write" => AccessTraceOperation.Write,
        _ => throw new InvalidDataException(
            $"Unknown trace operation '{value}'."),
    };

    static byte? GetNullableByte(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetByte();

    static ulong? GetNullableUInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetUInt64();

    static long? GetNullableInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt64();
}
