// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly ref struct AccessTraceFrequencyFitInputs(
    ReadOnlySpan<double> values,
    double durationSeconds,
    double frequencyHz,
    bool[]? includedBins,
    double[] coefficients,
    double nuisanceIntercept,
    double nuisanceDrift)
{
    public ReadOnlySpan<double> Values { get; } = values;
    public double DurationSeconds { get; } = durationSeconds;
    public double FrequencyHz { get; } = frequencyHz;
    public bool[]? IncludedBins { get; } = includedBins;
    public double[] Coefficients { get; } = coefficients;
    public double NuisanceIntercept { get; } = nuisanceIntercept;
    public double NuisanceDrift { get; } = nuisanceDrift;
}
