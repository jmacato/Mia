// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceCategoricalFit(
    double Association,
    double R2,
    double FundamentalPower,
    double ColorHarmonicPower,
    double LcdFramePower,
    double LcdEdgePower,
    int FractionalPhaseIndex,
    IReadOnlyList<AccessTraceStateValueCount> StateValueCounts);
