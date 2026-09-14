// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceTargetPowers(
    double LoopPower,
    double ColorHarmonicPower,
    double StateRatePower,
    double LcdFramePower,
    double LcdEdgePower);
