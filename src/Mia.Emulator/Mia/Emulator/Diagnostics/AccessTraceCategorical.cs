// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceCategorical
{
    const string NoAccessLabel = "no-access";
    const string RareLabel = "rare";

    public static AccessTraceCategoricalFit Fit(
        ReadOnlySpan<ulong?> values,
        double durationSeconds,
        bool[] includedBins,
        double loopFrequencyHz = AccessTraceFrequencies.LoopHz)
    {
        Validate(values, includedBins);
        string[] labels = CreateLabels(values, includedBins);
        string[] categories = labels
            .Where((_, index) => includedBins[index])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var categoryIndexes = categories
            .Select((category, index) => (category, index))
            .ToDictionary(item => item.category, item => item.index, StringComparer.Ordinal);
        var phase = FindBestPhase((
            Labels: labels,
            CategoryIndexes: categoryIndexes,
            DurationSeconds: durationSeconds,
            IncludedBins: includedBins,
            LoopFrequencyHz: loopFrequencyHz));
        var powers = AccumulateSpectralPowers((
            Labels: labels,
            Categories: categories,
            DurationSeconds: durationSeconds,
            IncludedBins: includedBins));
        return new(
            Math.Max(0, phase.Association),
            Math.Max(0, phase.Association * phase.Association),
            powers.Fundamental,
            powers.Harmonic,
            powers.LcdFrame,
            powers.LcdEdge,
            phase.Index,
            ToDistribution(phase.Contingency!, categories));
    }

    static (long[][]? Contingency, double Association, int Index) FindBestPhase(
        (string[] Labels, Dictionary<string, int> CategoryIndexes,
            double DurationSeconds, bool[] IncludedBins,
            double LoopFrequencyHz) inputs)
    {
        (long[][]? Contingency, double Association, int Index) best =
            (null, double.NegativeInfinity, 0);
        for (var phaseIndex = 0;
             phaseIndex < AccessTraceFrequencies.FractionalPhaseSteps;
             phaseIndex++)
        {
            long[][] contingency = BuildContingency(new(
                inputs.Labels,
                inputs.CategoryIndexes,
                inputs.DurationSeconds,
                inputs.IncludedBins,
                phaseIndex,
                inputs.LoopFrequencyHz));
            var candidate = (
                Contingency: (long[][]?)contingency,
                Association: Association(contingency),
                Index: phaseIndex);
            best = PreferPhase(best, candidate);
        }
        return best;
    }

    static (long[][]? Contingency, double Association, int Index) PreferPhase(
        (long[][]? Contingency, double Association, int Index) current,
        (long[][]? Contingency, double Association, int Index) candidate) =>
        candidate.Association > current.Association ? candidate : current;

    static (double Fundamental, double Harmonic, double LcdFrame, double LcdEdge)
        AccumulateSpectralPowers((
            string[] Labels,
            string[] Categories,
            double DurationSeconds,
            bool[] IncludedBins) inputs)
    {
        (double Fundamental, double Harmonic, double LcdFrame, double LcdEdge)
            powers = default;
        foreach (string category in inputs.Categories)
        {
            double[] indicator = CreateIndicator(inputs.Labels, category);
            powers.Fundamental += FitPower(
                indicator,
                inputs.DurationSeconds,
                AccessTraceFrequencies.LoopHz,
                inputs.IncludedBins);
            powers.Harmonic += FitPower(
                indicator,
                inputs.DurationSeconds,
                AccessTraceFrequencies.ColorHarmonicHz,
                inputs.IncludedBins);
            powers.LcdFrame += FitPower(
                indicator,
                inputs.DurationSeconds,
                AccessTraceFrequencies.LcdFrameHz,
                inputs.IncludedBins);
            powers.LcdEdge += FitPower(
                indicator,
                inputs.DurationSeconds,
                AccessTraceFrequencies.LcdEdgeHz,
                inputs.IncludedBins);
        }
        return powers;
    }

    static double[] CreateIndicator(string[] labels, string category)
    {
        var indicator = new double[labels.Length];
        for (var index = 0; index < indicator.Length; index++)
        {
            indicator[index] = labels[index] == category ? 1 : 0;
        }
        return indicator;
    }

    static double FitPower(
        double[] indicator,
        double durationSeconds,
        double frequency,
        bool[] includedBins) => AccessTraceSpectral.Fit(
            indicator,
            durationSeconds,
            frequency,
            includedBins).Power;

    public static string[] CreateLabels(
        ReadOnlySpan<ulong?> values,
        bool[] includedBins)
    {
        Validate(values, includedBins);
        var rawCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long includedCount = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (!includedBins[index])
            {
                continue;
            }
            string label = FormatValue(values[index]);
            rawCounts[label] = rawCounts.GetValueOrDefault(label) + 1;
            includedCount++;
        }
        long rareLimit = Math.Max(2, includedCount / 1_000);
        var labels = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            ulong? value = values[index];
            string label = FormatValue(value);
            bool preserve = !value.HasValue || value.Value == 0 ||
                rawCounts.GetValueOrDefault(label) >= rareLimit;
            labels[index] = preserve
                ? label
                : RareLabel;
        }
        return labels;
    }

    public static double Association(long[][] contingency)
    {
        ArgumentNullException.ThrowIfNull(contingency);
        int rows = contingency.Length;
        int columns = rows == 0 ? 0 : contingency[0].Length;
        if (rows < 2 || columns < 2)
        {
            return 0;
        }
        var rowTotals = new long[rows];
        var columnTotals = new long[columns];
        long total = CalculateTotals(contingency, rowTotals, columnTotals);
        return total switch
        {
            0 or 1 => 0,
            _ => CalculateAssociation(
                contingency,
                rowTotals,
                columnTotals,
                total),
        };
    }

    static long CalculateTotals(
        long[][] contingency,
        long[] rowTotals,
        long[] columnTotals)
    {
        long total = 0;
        for (var row = 0; row < contingency.Length; row++)
        {
            total += CalculateRowTotals(
                contingency[row],
                rowTotals,
                columnTotals,
                row);
        }
        return total;
    }

    static long CalculateRowTotals(
        long[] contingencyRow,
        long[] rowTotals,
        long[] columnTotals,
        int row)
    {
        long total = 0;
        for (var column = 0; column < contingencyRow.Length; column++)
        {
            long count = contingencyRow[column];
            rowTotals[row] += count;
            columnTotals[column] += count;
            total += count;
        }
        return total;
    }

    static double CalculateAssociation(
        long[][] contingency,
        long[] rowTotals,
        long[] columnTotals,
        long total)
    {
        int rows = contingency.Length;
        int columns = columnTotals.Length;
        double chiSquared = 0;
        for (var row = 0; row < rows; row++)
        {
            chiSquared += CalculateRowChiSquared(
                contingency[row],
                rowTotals[row],
                columnTotals,
                total);
        }
        double phiSquared = chiSquared / total;
        double correctedPhiSquared = Math.Max(
            0,
            phiSquared - (double)(columns - 1) * (rows - 1) / (total - 1));
        double correctedRows = rows -
            (double)(rows - 1) * (rows - 1) / (total - 1);
        double correctedColumns = columns -
            (double)(columns - 1) * (columns - 1) / (total - 1);
        double dimension = Math.Min(
            correctedRows - 1,
            correctedColumns - 1);
        return dimension <= 0
            ? 0
            : Math.Sqrt(correctedPhiSquared / dimension);
    }

    static double CalculateRowChiSquared(
        long[] contingencyRow,
        long rowTotal,
        long[] columnTotals,
        long total)
    {
        double chiSquared = 0;
        for (var column = 0; column < contingencyRow.Length; column++)
        {
            double expected = (double)rowTotal * columnTotals[column] / total;
            double difference = contingencyRow[column] - expected;
            chiSquared += expected <= 0
                ? 0
                : difference * difference / expected;
        }
        return chiSquared;
    }

    public static double DistributionSimilarity(
        IReadOnlyList<AccessTraceStateValueCount> reference,
        IReadOnlyList<AccessTraceStateValueCount> observed,
        int cyclicOffset)
    {
        string[] categories = reference.Select(item => item.ValueLabel)
            .Concat(observed.Select(item => item.ValueLabel))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        double[][] first = ToProbabilityMatrix(reference, categories);
        double[][] second = ToProbabilityMatrix(observed, categories);
        double dot = 0;
        double firstPower = 0;
        double secondPower = 0;
        for (var state = 0; state < 6; state++)
        {
            int shiftedState = (state + cyclicOffset) % 6;
            for (var category = 0; category < categories.Length; category++)
            {
                double firstValue = first[state][category];
                double secondValue = second[shiftedState][category];
                dot += firstValue * secondValue;
                firstPower += firstValue * firstValue;
                secondPower += secondValue * secondValue;
            }
        }
        double denominator = Math.Sqrt(firstPower * secondPower);
        return denominator == 0 ? 0 : Math.Clamp(dot / denominator, 0, 1);
    }

    public static double[] BestFundamentalIndicator(
        ReadOnlySpan<ulong?> values,
        double durationSeconds,
        bool[] includedBins)
    {
        Validate(values, includedBins);
        string[] labels = CreateLabels(values, includedBins);
        double bestPower = double.NegativeInfinity;
        double[] best = new double[values.Length];
        foreach (string category in labels.Distinct(StringComparer.Ordinal))
        {
            var indicator = new double[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                indicator[index] = labels[index] == category ? 1 : 0;
            }
            double power = AccessTraceSpectral.Fit(
                indicator,
                durationSeconds,
                AccessTraceFrequencies.LoopHz,
                includedBins).Power;
            if (power > bestPower)
            {
                bestPower = power;
                best = indicator;
            }
        }
        return best;
    }

    public static double PhaseResultant(IEnumerable<double> phases)
    {
        double sine = 0;
        double cosine = 0;
        var count = 0;
        foreach (double phase in phases)
        {
            sine += Math.Sin(phase);
            cosine += Math.Cos(phase);
            count++;
        }
        return count == 0 ? 0 : Math.Sqrt(sine * sine + cosine * cosine) / count;
    }

    static AccessTraceStateValueCount[] ToDistribution(
        long[][] contingency,
        string[] categories)
    {
        var result = new List<AccessTraceStateValueCount>();
        for (var state = 0; state < contingency.Length; state++)
        {
            for (var category = 0; category < contingency[state].Length; category++)
            {
                result.Add(new(state, categories[category], contingency[state][category]));
            }
        }
        return [.. result];
    }

    static double[][] ToProbabilityMatrix(
        IReadOnlyList<AccessTraceStateValueCount> distribution,
        string[] categories)
    {
        var categoryIndexes = categories
            .Select((category, index) => (category, index))
            .ToDictionary(item => item.category, item => item.index, StringComparer.Ordinal);
        double[][] result = Enumerable.Range(0, 6)
            .Select(_ => new double[categories.Length])
            .ToArray();
        foreach (IGrouping<int, AccessTraceStateValueCount> state in distribution
                     .GroupBy(item => item.State))
        {
            long total = state.Sum(item => item.Count);
            if (total == 0)
            {
                continue;
            }
            foreach (AccessTraceStateValueCount item in state)
            {
                result[state.Key][categoryIndexes[item.ValueLabel]] =
                    (double)item.Count / total;
            }
        }
        return result;
    }

    static long[][] CreateLongRows(int rowCount, int columnCount) =>
        Enumerable.Range(0, rowCount)
            .Select(_ => new long[columnCount])
            .ToArray();

    static long[][] BuildContingency(AccessTraceContingencyInputs inputs)
    {
        long[][] result = CreateLongRows(6, inputs.CategoryIndexes.Count);
        foreach (int index in Enumerable.Range(0, inputs.Labels.Length)
                     .Where(index => inputs.IncludedBins[index]))
        {
            double seconds = (index + 0.5) * inputs.DurationSeconds /
                inputs.Labels.Length;
            double loopPhase = seconds * inputs.LoopFrequencyHz;
            int cell = (int)Math.Floor(
                (loopPhase - Math.Floor(loopPhase)) *
                AccessTraceFrequencies.PhaseCellCount);
            int shiftedCell = (cell + inputs.PhaseIndex) %
                AccessTraceFrequencies.PhaseCellCount;
            int state = shiftedCell * 6 /
                AccessTraceFrequencies.PhaseCellCount;
            result[state][inputs.CategoryIndexes[inputs.Labels[index]]]++;
        }
        return result;
    }

    static string FormatValue(ulong? value) => value.HasValue
        ? $"0x{value.Value:x}"
        : NoAccessLabel;

    static void Validate(ReadOnlySpan<ulong?> values, bool[] includedBins)
    {
        if (values.Length < 5 || includedBins.Length != values.Length)
        {
            throw new ArgumentException("Invalid categorical series.", nameof(values));
        }
    }
}
