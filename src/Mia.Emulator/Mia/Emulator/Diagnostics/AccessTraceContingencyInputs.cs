// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceContingencyInputs(
    string[] Labels,
    Dictionary<string, int> CategoryIndexes,
    double DurationSeconds,
    bool[] IncludedBins,
    int PhaseIndex,
    double LoopFrequencyHz);
