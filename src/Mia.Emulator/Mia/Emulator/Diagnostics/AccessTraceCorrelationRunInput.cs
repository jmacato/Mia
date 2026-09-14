// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceCorrelationRunInput(
    string PairId,
    bool IsService,
    double DurationSeconds,
    long[] EventCounts,
    long[] TotalTraffic,
    ulong?[] HeldValues);
