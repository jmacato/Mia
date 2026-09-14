// SPDX-License-Identifier: MIT

using System.Text.Json;
using Mia.Emulator.Diagnostics;
using Xunit;

namespace AvrCore.Tests;

public sealed class AccessTraceDeterministicTests
{
    [Fact]
    public void AccessTraceDeterministicLongRunHasBoundedAggregateStorageAndOutput()
    {
        const int eventCount = 1_000_000;
        const long duration = 2_000_000;
        AccessTraceAccumulator accumulator = CreateAccumulator(0, duration);
        byte previous = 0;
        for (var index = 0; index < eventCount; index++)
        {
            byte value = (byte)(index & 3);
            accumulator.RecordAvrWrite(
                index * 2L,
                0x1234,
                0x4567,
                previous,
                value,
                0xff);
            previous = value;
        }
        accumulator.RecordAvrWrite(
            duration,
            0x1234,
            0x4567,
            previous,
            0,
            0xff);

        AccessTraceSnapshot snapshot = accumulator.Snapshot();
        byte[] first = Write(snapshot);
        byte[] second = Write(snapshot);

        Assert.Equal(first, second);
        Assert.Equal(1, snapshot.Storage.AddressCount);
        Assert.Equal(AccessTraceWindow.RequiredBinCount,
            snapshot.Storage.TimeBinCellCount);
        Assert.Equal(AccessTraceWindow.RequiredBinCount * 4,
            snapshot.Storage.BinValueCellCount);
        Assert.Equal(4, snapshot.Storage.ValueHistogramCellCount);
        Assert.Equal(5, snapshot.Storage.TransitionCellCount);
        Assert.Equal(1, snapshot.Storage.ProgramCounterCellCount);
        Assert.True(first.Length < 1_000_000, $"aggregate bytes={first.Length:n0}");
        using JsonDocument document = JsonDocument.Parse(first);
        Assert.Equal(
            AccessTraceSnapshot.CurrentSchema,
            document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(
            AccessTraceWindow.RequiredBinCount,
            document.RootElement
                .GetProperty("window")
                .GetProperty("binCount")
                .GetInt32());
        Assert.Equal(
            1,
            document.RootElement
                .GetProperty("reconciliation")[1]
                .GetProperty("outsideWindowCount")
                .GetInt64());
    }

    [Fact]
    public void AccessTraceDeterministicClockConversionIsExactAndOverflowSafe()
    {
        Assert.Equal(0, AccessTraceClockConversion.AvrToAsic(0));
        Assert.Equal(13, AccessTraceClockConversion.AvrToAsic(12));
        Assert.Equal(26, AccessTraceClockConversion.AvrToAsic(24));
        Assert.Equal(130_000_000,
            AccessTraceClockConversion.AvrToAsic(120_000_000));
        Assert.Equal(
            13L * (long.MaxValue / 13 / 12),
            AccessTraceClockConversion.AvrToAsic(
                12L * (long.MaxValue / 13 / 12)));
    }

    [Fact]
    public void AccessTraceDeterministicWindowUsesAllFixedBinsWithoutOverlap()
    {
        var window = new AccessTraceWindow(100, 2_148);

        Assert.Equal(0, window.GetBinIndex(100));
        Assert.Equal(1, window.GetBinIndex(101));
        Assert.Equal(2_047, window.GetBinIndex(2_147));
        Assert.False(window.Contains(2_148));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => window.GetBinIndex(2_148));
    }

    [Fact]
    public void AccessTraceDeterministicWideEntropyUsesFixedOverflowBuckets()
    {
        const int eventCount = 1_000_000;
        AccessTraceAccumulator accumulator = CreateAccumulator(0, eventCount);
        for (var index = 0; index < eventCount; index++)
        {
            accumulator.RecordArmMmio(new(
                index,
                (uint)index,
                0x00800908,
                4,
                IsWrite: true,
                (uint)index));
        }

        AccessTraceSnapshot snapshot = accumulator.Snapshot();
        AccessTraceOperationSnapshot operation = Assert.Single(
            Assert.Single(snapshot.Addresses).Operations);
        byte[] encoded = Write(snapshot);

        Assert.Equal(eventCount, operation.Count);
        Assert.Equal(AccessTraceLimits.WideValueCells, operation.Values.Count);
        Assert.Equal(
            AccessTraceLimits.WideTransitionCells,
            operation.Transitions.Count);
        Assert.Equal(
            AccessTraceLimits.ProgramCounterCells,
            operation.ProgramCounters.Count);
        Assert.True(operation.ValueOverflowCount > 0);
        Assert.True(operation.TransitionOverflowCount > 0);
        Assert.True(operation.ProgramCounterOverflowCount > 0);
        Assert.True(operation.BinValueOverflowCount > 0);
        Assert.True(
            snapshot.Storage.BinValueCellCount <=
            AccessTraceWindow.RequiredBinCount *
            AccessTraceLimits.WideBinValueCells);
        Assert.True(encoded.Length < 3_000_000, $"aggregate bytes={encoded.Length:n0}");
        using var stream = new MemoryStream(encoded);
        AccessTraceSnapshot decoded = AccessTraceJsonReader.Read(stream);
        Assert.Equal(snapshot.Storage, decoded.Storage);
        Assert.Equal(
            snapshot.Reconciliation,
            decoded.Reconciliation);
    }

    static byte[] Write(AccessTraceSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        AccessTraceJson.Write(stream, snapshot);
        return stream.ToArray();
    }

    static AccessTraceAccumulator CreateAccumulator(long start, long end) => new(
        new(start, end),
        new(
            "asic-13mhz",
            13_000_000,
            12_000_000,
            13,
            12,
            0));
}
