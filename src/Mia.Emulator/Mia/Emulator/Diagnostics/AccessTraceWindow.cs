// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceWindow
{
    public const int RequiredBinCount = 2_048;

    public AccessTraceWindow(
        long startCycle,
        long endCycleExclusive,
        int binCount = RequiredBinCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startCycle);
        if (endCycleExclusive <= startCycle)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endCycleExclusive),
                "The trace window must contain at least one machine cycle.");
        }
        if (binCount != RequiredBinCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(binCount),
                $"Access traces require exactly {RequiredBinCount:n0} time bins.");
        }

        StartCycle = startCycle;
        EndCycleExclusive = endCycleExclusive;
        BinCount = binCount;
    }

    public long StartCycle { get; }

    public long EndCycleExclusive { get; }

    public int BinCount { get; }

    public long DurationCycles => EndCycleExclusive - StartCycle;

    public bool Contains(long cycle) =>
        cycle >= StartCycle && cycle < EndCycleExclusive;

    public int GetBinIndex(long cycle)
    {
        if (!Contains(cycle))
        {
            throw new ArgumentOutOfRangeException(nameof(cycle));
        }

        ulong offset = (ulong)(cycle - StartCycle);
        ulong duration = (ulong)DurationCycles;
        return (int)(((UInt128)offset * (uint)BinCount) / duration);
    }
}
