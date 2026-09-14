// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceSeriesBuilder
{
    public static long[] ExpandEventCounts(
        AccessTraceOperationSnapshot operation,
        int binCount = AccessTraceWindow.RequiredBinCount)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = new long[binCount];
        foreach (AccessTraceTimeBinSnapshot bin in operation.TimeBins)
        {
            ValidateBin(bin.Index, binCount);
            result[bin.Index] = bin.EventCount;
        }
        return result;
    }

    public static long[] ExpandChangeCounts(
        AccessTraceOperationSnapshot operation,
        int binCount = AccessTraceWindow.RequiredBinCount)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = new long[binCount];
        foreach (AccessTraceTimeBinSnapshot bin in operation.TimeBins)
        {
            ValidateBin(bin.Index, binCount);
            result[bin.Index] = bin.ChangeCount;
        }
        return result;
    }

    public static long[] ExpandValueCounts(
        AccessTraceOperationSnapshot operation,
        ulong value,
        int binCount = AccessTraceWindow.RequiredBinCount)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = new long[binCount];
        foreach (AccessTraceTimeBinSnapshot bin in operation.TimeBins)
        {
            ValidateBin(bin.Index, binCount);
            AccessTraceBinValueCount? match = bin.Values
                .FirstOrDefault(item => item.Value == value);
            if (match.HasValue)
            {
                result[bin.Index] = match.Value.Count;
            }
        }
        return result;
    }

    /// <summary>
    /// Expands sparse event bins into all fixed bins. A null entry means that
    /// no initial or observed value exists; it is distinct from value zero.
    /// </summary>
    public static ulong?[] ExpandHeldValues(
        AccessTraceOperationSnapshot operation,
        int binCount = AccessTraceWindow.RequiredBinCount)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var byIndex = operation.TimeBins.ToDictionary(bin => bin.Index);
        var result = new ulong?[binCount];
        ulong? heldValue = operation.InitialHeldValue;
        for (var index = 0; index < binCount; index++)
        {
            if (byIndex.TryGetValue(index, out AccessTraceTimeBinSnapshot? bin))
            {
                heldValue = bin.LastValue;
            }
            result[index] = heldValue;
        }
        return result;
    }

    static void ValidateBin(int index, int binCount)
    {
        if ((uint)index >= (uint)binCount)
        {
            throw new InvalidDataException(
                $"Time-bin index {index} is outside 0..{binCount - 1}.");
        }
    }
}
