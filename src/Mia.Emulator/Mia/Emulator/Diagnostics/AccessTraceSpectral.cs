// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceSpectral
{
    const double SingularTolerance = 1e-12;

    public static double[] NormalizeByTotalTraffic(
        ReadOnlySpan<long> values,
        ReadOnlySpan<long> totalTraffic)
    {
        if (values.Length != totalTraffic.Length)
        {
            throw new ArgumentException(
                "Value and total-traffic bins must have equal lengths.",
                nameof(totalTraffic));
        }
        var result = new double[values.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = totalTraffic[index] == 0
                ? double.NaN
                : (double)values[index] / totalTraffic[index];
        }
        return result;
    }

    public static AccessTraceFrequencyFit Fit(
        ReadOnlySpan<double> values,
        double durationSeconds,
        double frequencyHz,
        bool[]? includedBins = null)
    {
        Validate(values, durationSeconds, frequencyHz, includedBins);
        (double[] normal, double[] nuisance, int includedCount) =
            AccumulateFrequencyFit(
                values,
                durationSeconds,
                frequencyHz,
                includedBins);
        if (includedCount < 5 || !Solve4(normal, out double[] coefficients))
        {
            return EmptyFit(frequencyHz, includedCount);
        }
        (double nuisanceIntercept, double nuisanceDrift) =
            SolveNuisance(nuisance);
        (double nuisanceError, double fullError) = CalculateFitErrors(
            new AccessTraceFrequencyFitInputs(
                values,
                durationSeconds,
                frequencyHz,
                includedBins,
                coefficients,
                nuisanceIntercept,
                nuisanceDrift));
        double amplitude = Math.Sqrt(
            coefficients[2] * coefficients[2] +
            coefficients[3] * coefficients[3]);
        double r2 = nuisanceError <= SingularTolerance
            ? 0
            : Math.Clamp(1d - fullError / nuisanceError, 0, 1);
        return new(
            frequencyHz,
            coefficients[0],
            coefficients[1],
            coefficients[2],
            coefficients[3],
            amplitude,
            Math.Atan2(coefficients[2], coefficients[3]),
            amplitude * amplitude,
            r2,
            includedCount);
    }

    static (double[] Normal, double[] Nuisance, int IncludedCount)
        AccumulateFrequencyFit(
            ReadOnlySpan<double> values,
            double durationSeconds,
            double frequencyHz,
            bool[]? includedBins)
    {
        var normal = new double[20];
        var nuisance = new double[6];
        Span<double> row = stackalloc double[4];
        int includedCount = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (includedBins is not null && !includedBins[index])
            {
                continue;
            }
            if (!double.IsFinite(values[index]))
            {
                continue;
            }
            double weight = Hann(index, values.Length);
            if (weight == 0)
            {
                continue;
            }
            double normalizedTime = values.Length == 1
                ? 0
                : 2d * index / (values.Length - 1) - 1d;
            double seconds = (index + 0.5) * durationSeconds / values.Length;
            double angle = 2d * Math.PI * frequencyHz * seconds;
            row[0] = 1;
            row[1] = normalizedTime;
            row[2] = Math.Sin(angle);
            row[3] = Math.Cos(angle);
            AccumulateNormal(normal, row, values[index], weight);
            AccumulateNuisance(
                nuisance,
                normalizedTime,
                values[index],
                weight);
            includedCount++;
        }
        return (normal, nuisance, includedCount);
    }

    static (double NuisanceError, double FullError) CalculateFitErrors(
        AccessTraceFrequencyFitInputs inputs)
    {
        double nuisanceError = 0;
        double fullError = 0;
        for (var index = 0; index < inputs.Values.Length; index++)
        {
            if (inputs.IncludedBins is not null &&
                !inputs.IncludedBins[index])
            {
                continue;
            }
            if (!double.IsFinite(inputs.Values[index]))
            {
                continue;
            }
            double weight = Hann(index, inputs.Values.Length);
            double normalizedTime = inputs.Values.Length == 1
                ? 0
                : 2d * index / (inputs.Values.Length - 1) - 1d;
            double seconds = (index + 0.5) * inputs.DurationSeconds /
                inputs.Values.Length;
            double angle = 2d * Math.PI * inputs.FrequencyHz * seconds;
            double nuisanceResidual = inputs.Values[index] -
                inputs.NuisanceIntercept -
                inputs.NuisanceDrift * normalizedTime;
            double fullResidual = inputs.Values[index] -
                inputs.Coefficients[0] -
                inputs.Coefficients[1] * normalizedTime -
                inputs.Coefficients[2] * Math.Sin(angle) -
                inputs.Coefficients[3] * Math.Cos(angle);
            nuisanceError += weight * nuisanceResidual * nuisanceResidual;
            fullError += weight * fullResidual * fullResidual;
        }
        return (nuisanceError, fullError);
    }

    public static double[] DetrendAndHann(
        ReadOnlySpan<double> values,
        bool[]? includedBins = null)
    {
        if (values.Length == 0 ||
            (includedBins is not null && includedBins.Length != values.Length))
        {
            throw new ArgumentException("Invalid detrending input.", nameof(values));
        }
        double[] nuisance = AccumulateDetrending(values, includedBins);
        (double intercept, double drift) = SolveNuisance(nuisance);
        return ApplyDetrending(values, includedBins, intercept, drift);
    }

    static double[] AccumulateDetrending(
        ReadOnlySpan<double> values,
        bool[]? includedBins)
    {
        var nuisance = new double[6];
        for (var index = 0; index < values.Length; index++)
        {
            if (includedBins is not null && !includedBins[index])
            {
                continue;
            }
            if (!double.IsFinite(values[index]))
            {
                continue;
            }
            double normalizedTime = values.Length == 1
                ? 0
                : 2d * index / (values.Length - 1) - 1d;
            AccumulateNuisance(
                nuisance,
                normalizedTime,
                values[index],
                Hann(index, values.Length));
        }
        return nuisance;
    }

    static double[] ApplyDetrending(
        ReadOnlySpan<double> values,
        bool[]? includedBins,
        double intercept,
        double drift)
    {
        var result = new double[values.Length];
        for (var index = 0; index < result.Length; index++)
        {
            if (includedBins is not null && !includedBins[index])
            {
                continue;
            }
            if (!double.IsFinite(values[index]))
            {
                result[index] = double.NaN;
                continue;
            }
            double normalizedTime = values.Length == 1
                ? 0
                : 2d * index / (values.Length - 1) - 1d;
            result[index] =
                (values[index] - intercept - drift * normalizedTime) *
                Hann(index, values.Length);
        }
        return result;
    }

    public static double EmpiricalPValue(
        double targetPower,
        ReadOnlySpan<double> offFrequencyPowers)
    {
        if (offFrequencyPowers.Length == 0)
        {
            throw new ArgumentException(
                "At least one off-frequency power is required.",
                nameof(offFrequencyPowers));
        }
        var exceedances = 0;
        foreach (double power in offFrequencyPowers)
        {
            if (power >= targetPower)
            {
                exceedances++;
            }
        }
        return (exceedances + 1d) / (offFrequencyPowers.Length + 1d);
    }

    static void Validate(
        ReadOnlySpan<double> values,
        double durationSeconds,
        double frequencyHz,
        bool[]? includedBins)
    {
        ValidateBinCount(values);
        ValidatePositiveFinite(durationSeconds, nameof(durationSeconds));
        ValidatePositiveFinite(frequencyHz, nameof(frequencyHz));
        ValidateInclusionMask(values, includedBins);
    }

    static void ValidateBinCount(ReadOnlySpan<double> values)
    {
        if (values.Length < 5)
        {
            throw new ArgumentException(
                "Frequency regression requires at least five bins.",
                nameof(values));
        }
    }

    static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    static void ValidateInclusionMask(
        ReadOnlySpan<double> values,
        bool[]? includedBins)
    {
        if (includedBins is not null && includedBins.Length != values.Length)
        {
            throw new ArgumentException(
                "The inclusion mask must match the bin count.",
                nameof(includedBins));
        }
    }

    static double Hann(int index, int count) => count == 1
        ? 1
        : 0.5d - 0.5d * Math.Cos(2d * Math.PI * index / (count - 1));

    static void AccumulateNormal(
        double[] normal,
        ReadOnlySpan<double> row,
        double value,
        double weight)
    {
        for (var column = 0; column < 4; column++)
        {
            for (var other = 0; other < 4; other++)
            {
                normal[column * 5 + other] +=
                    weight * row[column] * row[other];
            }
            normal[column * 5 + 4] += weight * row[column] * value;
        }
    }

    static void AccumulateNuisance(
        double[] normal,
        double time,
        double value,
        double weight)
    {
        normal[0] += weight;
        normal[1] += weight * time;
        normal[2] += weight * value;
        normal[3] += weight * time * time;
        normal[4] += weight * time * value;
        normal[5] += weight * value * value;
    }

    static (double Intercept, double Drift) SolveNuisance(double[] normal)
    {
        double determinant = normal[0] * normal[3] - normal[1] * normal[1];
        if (Math.Abs(determinant) <= SingularTolerance)
        {
            return (0, 0);
        }
        return (
            (normal[2] * normal[3] - normal[1] * normal[4]) / determinant,
            (normal[0] * normal[4] - normal[1] * normal[2]) / determinant);
    }

    static bool Solve4(double[] augmented, out double[] result)
    {
        var matrix = (double[])augmented.Clone();
        for (var pivot = 0; pivot < 4; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < 4; row++)
            {
                if (Math.Abs(matrix[row * 5 + pivot]) >
                    Math.Abs(matrix[best * 5 + pivot]))
                {
                    best = row;
                }
            }
            if (Math.Abs(matrix[best * 5 + pivot]) <= SingularTolerance)
            {
                result = [];
                return false;
            }
            SwapRows(matrix, pivot, best);
            double divisor = matrix[pivot * 5 + pivot];
            for (var column = pivot; column < 5; column++)
            {
                matrix[pivot * 5 + column] /= divisor;
            }
            for (var row = 0; row < 4; row++)
            {
                if (row == pivot)
                {
                    continue;
                }
                double factor = matrix[row * 5 + pivot];
                for (var column = pivot; column < 5; column++)
                {
                    matrix[row * 5 + column] -=
                        factor * matrix[pivot * 5 + column];
                }
            }
        }
        result = new double[4];
        for (var row = 0; row < 4; row++)
        {
            result[row] = matrix[row * 5 + 4];
        }
        return true;
    }

    static void SwapRows(double[] matrix, int first, int second)
    {
        if (first == second)
        {
            return;
        }
        for (var column = 0; column < 5; column++)
        {
            (matrix[first * 5 + column], matrix[second * 5 + column]) =
                (matrix[second * 5 + column], matrix[first * 5 + column]);
        }
    }

    static AccessTraceFrequencyFit EmptyFit(double frequencyHz, int count) =>
        new(frequencyHz, 0, 0, 0, 0, 0, 0, 0, 0, count);
}
