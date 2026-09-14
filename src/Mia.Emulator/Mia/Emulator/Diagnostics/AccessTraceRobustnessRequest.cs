// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceRobustnessRequest(
    IReadOnlyList<AccessTraceCorrelationRunInput> Inputs,
    int ExcludedLoops,
    AccessTraceWindowHalf Half,
    double BaselineEffect,
    AccessTraceRunCorrelation[] PrimaryRuns);
