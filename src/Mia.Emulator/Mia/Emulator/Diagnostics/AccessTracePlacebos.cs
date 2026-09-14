// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTracePlacebos
{
    public static double[] Create(
        double durationSeconds,
        int count = AccessTraceFrequencies.MinimumPlaceboFundamentals)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(
            count,
            AccessTraceFrequencies.MinimumPlaceboFundamentals);

        double resolution = 1d / durationSeconds;
        double nyquist = AccessTraceWindow.RequiredBinCount /
            (2d * durationSeconds);
        double minimum = Math.Max(0.05, resolution / 2);
        double maximum = nyquist * 0.49;
        if (maximum <= minimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "The trace window has no placebo band below Nyquist.");
        }
        var result = new List<double>(count);
        var observedBits = new HashSet<long>();
        for (long ordinal = 1; result.Count < count; ordinal++)
        {
            double fraction = ordinal * 0.6180339887498949;
            fraction -= Math.Floor(fraction);
            double frequency = minimum + fraction * (maximum - minimum);
            if (IsNearTarget(frequency, resolution) ||
                !observedBits.Add(BitConverter.DoubleToInt64Bits(frequency)))
            {
                continue;
            }
            result.Add(frequency);
        }
        result.Sort();
        return [.. result];
    }

    static bool IsNearTarget(double frequency, double resolution) =>
        IsNearExactTarget(frequency, resolution) ||
        IsNearExactTarget(frequency * 2, resolution) ||
        Math.Abs(frequency - AccessTraceFrequencies.LcdFrameHz) <= resolution ||
        Math.Abs(frequency - AccessTraceFrequencies.LcdEdgeHz) <= resolution ||
        Math.Abs(frequency * 2 - AccessTraceFrequencies.LcdFrameHz) <= resolution ||
        Math.Abs(frequency * 2 - AccessTraceFrequencies.LcdEdgeHz) <= resolution;

    static bool IsNearExactTarget(double frequency, double resolution) =>
        Math.Abs(frequency - AccessTraceFrequencies.LoopHz) <= resolution ||
        Math.Abs(frequency - AccessTraceFrequencies.ColorHarmonicHz) <= resolution ||
        Math.Abs(frequency - AccessTraceFrequencies.StateRateHz) <= resolution;
}
