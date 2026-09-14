// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceFrequencies
{
    public const double LoopHz = 1.101895735;
    public const double ColorHarmonicHz = 2.203791470;
    public const double StateRateHz = 6.611374410;
    public const double LcdFrameHz = 0.831889081;
    public const double LcdEdgeHz = 1.664221756;
    public const int PhaseCellCount = 96;
    public const int FractionalPhaseSteps = PhaseCellCount / 6;
    public const double LoopUncertaintyHz = 0.0005;
    public const int MinimumPlaceboFundamentals = 200;
    public const int MinimumRotationNulls = 9_999;

    public static double[] LoopFrequencyGrid() =>
    [
        LoopHz - LoopUncertaintyHz,
        LoopHz,
        LoopHz + LoopUncertaintyHz,
    ];
}
