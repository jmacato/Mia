// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceInclusionRequest(
    int BinCount,
    double DurationSeconds,
    int ExcludedWholeLoops,
    AccessTraceWindowHalf Half,
    double LoopFrequencyHz = AccessTraceFrequencies.LoopHz);
