// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceCorrelationAnalyzer
{
    const double FalseDiscoveryRate = 0.05;
    const double MinimumAmplitudeRatio = 2;
    const double MinimumR2 = 0.02;
    const double MinimumPhaseResultant = 0.70;

    internal const string EnvironmentVariableName = "MIA_CORRELATION_DOP";

    internal static readonly int DegreeOfParallelism = Math.Max(1,
        Environment.GetEnvironmentVariable(EnvironmentVariableName) is { } raw
            && int.TryParse(raw, out var requested)
            ? Math.Min(requested, Math.Max(1, Environment.ProcessorCount - 2))
            : Math.Max(1, Environment.ProcessorCount - 2));
    const double MinimumAbsoluteEffect = 0.05;
    const double MinimumStandardizedEffect = 3;

    public static AccessTraceCorrelationReport Analyze(
        IReadOnlyList<AccessTraceCorrelationCandidateInput> candidates,
        bool hasExternalPhaseAnchor = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateCandidates(candidates);

        string[] inventory = candidates
            .Select(candidate => candidate.CandidateId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueInventory(inventory, candidates);
        return AnalyzeValidatedCandidates(
            candidates,
            inventory,
            hasExternalPhaseAnchor);
    }

    static void ValidateCandidates(
        IReadOnlyList<AccessTraceCorrelationCandidateInput> candidates)
    {
        foreach (AccessTraceCorrelationCandidateInput candidate in candidates)
        {
            Validate(candidate);
        }
    }

    static void EnsureUniqueInventory(
        string[] inventory,
        IReadOnlyList<AccessTraceCorrelationCandidateInput> candidates)
    {
        if (inventory.Distinct(StringComparer.Ordinal).Count() != inventory.Length)
        {
            throw new ArgumentException(
                "Candidate identifiers must be unique.",
                nameof(candidates));
        }
    }

    static AccessTraceCorrelationReport AnalyzeValidatedCandidates(
        IReadOnlyList<AccessTraceCorrelationCandidateInput> candidates,
        string[] inventory,
        bool hasExternalPhaseAnchor)
    {
        int nullCount = AccessTraceFrequencies.MinimumRotationNulls;
        int placeboCount = AccessTraceFrequencies.MinimumPlaceboFundamentals;
        var provisional = candidates
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(DegreeOfParallelism)
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(candidate => AnalyzeCandidate(
                candidate,
                nullCount,
                placeboCount,
                hasExternalPhaseAnchor))
            .ToArray();
        IReadOnlyDictionary<string, double> adjusted =
            AccessTraceMultipleTesting.BenjaminiHochberg(
                provisional.Select(candidate => (
                    candidate.CandidateId,
                    candidate.RotationPValue)));
        AccessTraceCandidateCorrelation[] completed = provisional
            .Select(candidate =>
            {
                double q = adjusted[candidate.CandidateId];
                bool thresholdsPass = candidate.AllPairEffectsPositive &&
                    candidate.MedianServiceControlEffect >= MinimumAbsoluteEffect &&
                    candidate.NullStandardizedEffect >= MinimumStandardizedEffect &&
                    candidate.OffFrequencyPValue <= FalseDiscoveryRate &&
                    candidate.MedianServiceControlRatio >= MinimumAmplitudeRatio &&
                    candidate.MedianServiceR2 >= MinimumR2 &&
                    candidate.PhaseResultant >= MinimumPhaseResultant &&
                    TargetPowersPass(candidate.Runs) &&
                    AccessRatePowersPass(candidate.Runs) &&
                    LedVsLcdPass(candidate.Runs) &&
                    candidate.Runs.Where(run => run.IsService).All(run =>
                        run.CyclicTemplateCorrelation >= MinimumPhaseResultant) &&
                    candidate.Robustness.All(result =>
                        result.AllPairEffectsPositive &&
                        result.MedianServiceControlRatio >= MinimumAmplitudeRatio &&
                        result.RelativeEffectChange <= 0.25 &&
                        result.MedianServiceR2 >= MinimumR2 &&
                        result.PhaseResultant >= MinimumPhaseResultant &&
                        result.TargetPowersPass &&
                        result.ValuePhasePass);
                return candidate with
                {
                    AdjustedQValue = q,
                    Accepted = thresholdsPass && q <= FalseDiscoveryRate,
                };
            })
            .ToArray();
        return new(
            inventory,
            completed,
            placeboCount,
            nullCount,
            FalseDiscoveryRate);
    }

    static AccessTraceCandidateCorrelation AnalyzeCandidate(
        AccessTraceCorrelationCandidateInput candidate,
        int nullCount,
        int placeboCount,
        bool hasExternalPhaseAnchor)
    {
        AccessTraceRunCorrelation[] runs = AlignCyclicPhases(candidate.Runs
            .OrderBy(run => run.PairId, StringComparer.Ordinal)
            .ThenBy(run => run.IsService)
            .Select(run => AnalyzeRun(run, 2, AccessTraceWindowHalf.Full))
            .ToArray());
        (double effect, double ratio, bool allPositive) = ComparePairs(runs);
        double medianR2 = Median(runs
            .Where(run => run.IsService)
            .Select(run => run.CategoricalR2));
        double phaseResultant = AccessTraceCategorical.PhaseResultant(runs
            .Where(run => run.IsService)
            .Select(run => run.CyclicPhaseRadians));
        AccessTraceRobustnessResult[] robustness =
        [
            AnalyzeRobustness(new(candidate.Runs, 1, AccessTraceWindowHalf.Full, effect, runs)),
            AnalyzeRobustness(new(candidate.Runs, 2, AccessTraceWindowHalf.Full, effect, runs)),
            AnalyzeRobustness(new(candidate.Runs, 4, AccessTraceWindowHalf.Full, effect, runs)),
            AnalyzeRobustness(new(candidate.Runs, 2, AccessTraceWindowHalf.First, effect, runs)),
            AnalyzeRobustness(new(candidate.Runs, 2, AccessTraceWindowHalf.Second, effect, runs)),
        ];

        double duration = candidate.Runs[0].DurationSeconds;
        double[] placeboFrequencies = AccessTracePlacebos.Create(duration, placeboCount);
        (double targetPower, double[] placeboPowers) = AnalyzePlacebos(
            candidate.Runs,
            placeboFrequencies);
        double offP = AccessTraceSpectral.EmpiricalPValue(
            targetPower,
            placeboPowers);
        AccessTraceRotationResult rotation = RotationPValue(
            candidate.Runs,
            nullCount);
        if (Math.Abs(rotation.ObservedEffect - effect) > 1e-9)
        {
            throw new InvalidOperationException(
                "The observed and rotation statistics use different units.");
        }

        return new(
            candidate.CandidateId,
            runs,
            robustness,
            placeboFrequencies,
            placeboPowers,
            effect,
            ratio,
            allPositive,
            medianR2,
            phaseResultant,
            offP,
            rotation.PValue,
            rotation.StandardizedEffect,
            AdjustedQValue: 1,
            nullCount,
            Accepted: false,
            hasExternalPhaseAnchor,
            AnchoredStateNames: hasExternalPhaseAnchor
                ? "blue,blue+red,blue+green,off,red,green"
                : null);
    }

    static AccessTraceRunCorrelation AnalyzeRun(
        AccessTraceCorrelationRunInput run,
        int excludedLoops,
        AccessTraceWindowHalf half)
    {
        bool[] included = AccessTraceInclusion.Create(new(
            run.EventCounts.Length,
            run.DurationSeconds,
            excludedLoops,
            half));
        double[] rawAccess = run.EventCounts
            .Select(count => (double)count)
            .ToArray();
        double[] trafficShare = AccessTraceSpectral.NormalizeByTotalTraffic(
            run.EventCounts,
            run.TotalTraffic);
        AccessTraceTargetPowers rawPowers = FitAccessPowers(
            rawAccess,
            run.DurationSeconds,
            included);
        AccessTraceTargetPowers sharePowers = FitAccessPowers(
            trafficShare,
            run.DurationSeconds,
            included);
        (double Frequency, AccessTraceCategoricalFit Fit) categorical =
            AccessTraceFrequencies.LoopFrequencyGrid()
                .Select(frequency =>
                {
                    bool[] frequencyIncluded = AccessTraceInclusion.Create(new(
                        run.EventCounts.Length,
                        run.DurationSeconds,
                        excludedLoops,
                        half,
                        frequency));
                    frequencyIncluded = PrepareCategoricalMask(
                        run,
                        frequencyIncluded);
                    return (
                        Frequency: frequency,
                        Fit: AccessTraceCategorical.Fit(
                            run.HeldValues,
                            run.DurationSeconds,
                            frequencyIncluded,
                            frequency));
                })
                .OrderByDescending(item => item.Fit.Association)
                .ThenBy(item => Math.Abs(
                    item.Frequency - AccessTraceFrequencies.LoopHz))
                .First();
        return new(
            run.PairId,
            run.IsService,
            rawPowers,
            sharePowers,
            categorical.Fit.Association,
            categorical.Fit.R2,
            categorical.Frequency,
            categorical.Fit.FundamentalPower,
            categorical.Fit.ColorHarmonicPower,
            categorical.Fit.LcdFramePower,
            categorical.Fit.LcdEdgePower,
            categorical.Fit.StateValueCounts,
            CyclicTemplateCorrelation: 0,
            CyclicOffset: 0,
            CyclicPhaseRadians: categorical.Fit.FractionalPhaseIndex *
                2d * Math.PI / AccessTraceFrequencies.PhaseCellCount);
    }

    static AccessTraceTargetPowers FitAccessPowers(
        ReadOnlySpan<double> series,
        double durationSeconds,
        bool[] included) => new(
        AccessTraceSpectral.Fit(
            series,
            durationSeconds,
            AccessTraceFrequencies.LoopHz,
            included).Power,
        AccessTraceSpectral.Fit(
            series,
            durationSeconds,
            AccessTraceFrequencies.ColorHarmonicHz,
            included).Power,
        AccessTraceSpectral.Fit(
            series,
            durationSeconds,
            AccessTraceFrequencies.StateRateHz,
            included).Power,
        AccessTraceSpectral.Fit(
            series,
            durationSeconds,
            AccessTraceFrequencies.LcdFrameHz,
            included).Power,
        AccessTraceSpectral.Fit(
            series,
            durationSeconds,
            AccessTraceFrequencies.LcdEdgeHz,
            included).Power);

    static bool[] PrepareCategoricalMask(
        AccessTraceCorrelationRunInput run,
        bool[] included)
    {
        bool[] result = (bool[])included.Clone();
        int firstAccess = Array.FindIndex(run.EventCounts, count => count > 0);
        if (firstAccess < 0)
        {
            return result;
        }
        for (var index = 0; index < firstAccess; index++)
        {
            if (!run.HeldValues[index].HasValue)
            {
                result[index] = false;
            }
        }
        return result;
    }

    static AccessTraceRobustnessResult AnalyzeRobustness(
        AccessTraceRobustnessRequest request)
    {
        AccessTraceRunCorrelation[] runs = AlignCyclicPhasesToPrimary(request.Inputs
            .Select(run => AnalyzeRun(run, request.ExcludedLoops, request.Half))
            .ToArray(), request.PrimaryRuns);
        (double effect, double ratio, bool allPositive) = ComparePairs(runs);
        double relativeChange = Math.Abs(effect - request.BaselineEffect) /
            Math.Max(Math.Abs(request.BaselineEffect), 1e-12);
        double medianR2 = Median(runs
            .Where(run => run.IsService)
            .Select(run => run.CategoricalR2));
        double phaseResultant = AccessTraceCategorical.PhaseResultant(runs
            .Where(run => run.IsService)
            .Select(run => run.CyclicPhaseRadians));
        bool valuePhasePass = runs.Where(run => run.IsService).All(run =>
        {
            AccessTraceRunCorrelation primary = request.PrimaryRuns.Single(item =>
                item.PairId == run.PairId && item.IsService == run.IsService);
            return run.CyclicTemplateCorrelation >= MinimumPhaseResultant &&
                CircularDistance(
                    run.CyclicPhaseRadians,
                    primary.CyclicPhaseRadians) <= Math.PI / 6;
        });
        return new(
            request.ExcludedLoops,
            request.Half,
            allPositive,
            ratio,
            effect,
            relativeChange,
            medianR2,
            phaseResultant,
            TargetPowersPass(runs) &&
                AccessRatePowersPass(runs) &&
                LedVsLcdPass(runs),
            valuePhasePass);
    }

    static AccessTraceRunCorrelation[] AlignCyclicPhases(
        AccessTraceRunCorrelation[] runs)
    {
        AccessTraceRunCorrelation reference = runs
            .Where(run => run.IsService)
            .OrderBy(run => run.PairId, StringComparer.Ordinal)
            .First();
        return runs.Select(run =>
        {
            int bestOffset = 0;
            double bestSimilarity = double.NegativeInfinity;
            for (var offset = 0; offset < 6; offset++)
            {
                double similarity = AccessTraceCategorical.DistributionSimilarity(
                    reference.StateValueCounts,
                    run.StateValueCounts,
                    offset);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestOffset = offset;
                }
            }
            return run with
            {
                CyclicTemplateCorrelation = Math.Max(0, bestSimilarity),
                CyclicOffset = bestOffset,
                CyclicPhaseRadians = (run.CyclicPhaseRadians +
                    bestOffset * 2d * Math.PI / 6) % (2d * Math.PI),
            };
        }).ToArray();
    }

    static AccessTraceRunCorrelation[] AlignCyclicPhasesToPrimary(
        AccessTraceRunCorrelation[] runs,
        AccessTraceRunCorrelation[] primaryRuns) => runs.Select(run =>
    {
        AccessTraceRunCorrelation reference = primaryRuns.Single(item =>
            item.PairId == run.PairId && item.IsService == run.IsService);
        int bestOffset = 0;
        double bestSimilarity = double.NegativeInfinity;
        for (var offset = 0; offset < 6; offset++)
        {
            double similarity = AccessTraceCategorical.DistributionSimilarity(
                reference.StateValueCounts,
                run.StateValueCounts,
                offset);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestOffset = offset;
            }
        }
        return run with
        {
            CyclicTemplateCorrelation = Math.Max(0, bestSimilarity),
            CyclicOffset = bestOffset,
            CyclicPhaseRadians = (run.CyclicPhaseRadians +
                bestOffset * 2d * Math.PI / 6) % (2d * Math.PI),
        };
    }).ToArray();

    static double CircularDistance(double first, double second)
    {
        double difference = Math.Abs(first - second) % (2d * Math.PI);
        return Math.Min(difference, 2d * Math.PI - difference);
    }

    static (double Effect, double Ratio, bool AllPositive) ComparePairs(
        IReadOnlyList<AccessTraceRunCorrelation> runs)
    {
        var effects = new List<double>();
        var ratios = new List<double>();
        foreach (IGrouping<string, AccessTraceRunCorrelation> pair in runs
                     .GroupBy(run => run.PairId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            AccessTraceRunCorrelation service = pair.Single(run => run.IsService);
            AccessTraceRunCorrelation control = pair.Single(run => !run.IsService);
            effects.Add(service.CategoricalAmplitude - control.CategoricalAmplitude);
            ratios.Add(Ratio(
                service.CategoricalAmplitude,
                control.CategoricalAmplitude));
        }
        return (
            Median(effects),
            Median(ratios),
            effects.Count == 3 && effects.All(effect => effect > 0));
    }

    static (double TargetPower, double[] PlaceboPowers) AnalyzePlacebos(
        IReadOnlyList<AccessTraceCorrelationRunInput> runs,
        IReadOnlyList<double> frequencies)
    {
        double targetPower = PairedCategoricalPowerEffect(
            runs,
            AccessTraceFrequencies.LoopHz);
        double[] placeboPowers = frequencies
            .Select(frequency => PairedCategoricalPowerEffect(runs, frequency))
            .ToArray();
        return (targetPower, placeboPowers);
    }

    static double PairedCategoricalPowerEffect(
        IReadOnlyList<AccessTraceCorrelationRunInput> runs,
        double fundamentalHz)
    {
        var runPowers = new List<(string PairId, bool IsService, double Power)>();
        foreach (AccessTraceCorrelationRunInput run in runs)
        {
            bool[] included = AccessTraceInclusion.Create(new(
                run.HeldValues.Length,
                run.DurationSeconds,
                2,
                AccessTraceWindowHalf.Full));
            included = PrepareCategoricalMask(run, included);
            string[] labels = AccessTraceCategorical.CreateLabels(
                run.HeldValues,
                included);
            double power = 0;
            foreach (string category in labels
                         .Where((_, index) => included[index])
                         .Distinct(StringComparer.Ordinal))
            {
                var indicator = new double[labels.Length];
                for (var index = 0; index < labels.Length; index++)
                {
                    indicator[index] = labels[index] == category ? 1 : 0;
                }
                power += AccessTraceSpectral.Fit(
                    indicator,
                    run.DurationSeconds,
                    fundamentalHz,
                    included).Power;
                power += AccessTraceSpectral.Fit(
                    indicator,
                    run.DurationSeconds,
                    fundamentalHz * 2,
                    included).Power;
            }
            runPowers.Add((run.PairId, run.IsService, power));
        }
        var effects = new List<double>();
        foreach (IGrouping<string, (string PairId, bool IsService, double Power)> pair
                 in runPowers.GroupBy(item => item.PairId, StringComparer.Ordinal))
        {
            effects.Add(
                pair.Single(item => item.IsService).Power -
                pair.Single(item => !item.IsService).Power);
        }
        return Median(effects);
    }

    static bool TargetPowersPass(IReadOnlyList<AccessTraceRunCorrelation> runs)
    {
        var ratios = new List<double>();
        var effects = new List<double>();
        foreach (IGrouping<string, AccessTraceRunCorrelation> pair in runs
                     .GroupBy(run => run.PairId, StringComparer.Ordinal))
        {
            AccessTraceRunCorrelation service = pair.Single(run => run.IsService);
            AccessTraceRunCorrelation control = pair.Single(run => !run.IsService);
            double servicePower = service.ConditionalLoopPower +
                service.ConditionalColorHarmonicPower;
            double controlPower = control.ConditionalLoopPower +
                control.ConditionalColorHarmonicPower;
            effects.Add(servicePower - controlPower);
            ratios.Add(Ratio(servicePower, controlPower));
        }
        return effects.Count == 3 && effects.All(effect => effect > 0) &&
            Median(ratios) >= MinimumAmplitudeRatio;
    }

    static bool AccessRatePowersPass(
        IReadOnlyList<AccessTraceRunCorrelation> runs)
    {
        var ratios = new List<double>();
        var effects = new List<double>();
        foreach (IGrouping<string, AccessTraceRunCorrelation> pair in runs
                     .GroupBy(run => run.PairId, StringComparer.Ordinal))
        {
            AccessTraceRunCorrelation service = pair.Single(run => run.IsService);
            AccessTraceRunCorrelation control = pair.Single(run => !run.IsService);
            double servicePower = service.AccessRatePowers.LoopPower +
                service.AccessRatePowers.ColorHarmonicPower +
                service.AccessRatePowers.StateRatePower;
            double controlPower = control.AccessRatePowers.LoopPower +
                control.AccessRatePowers.ColorHarmonicPower +
                control.AccessRatePowers.StateRatePower;
            effects.Add(servicePower - controlPower);
            ratios.Add(Ratio(servicePower, controlPower));
        }
        return effects.Count == 3 && effects.All(effect => effect > 0) &&
            Median(ratios) >= MinimumAmplitudeRatio;
    }

    static bool LedVsLcdPass(IReadOnlyList<AccessTraceRunCorrelation> runs)
    {
        var conditionalTargetEffects = new List<double>();
        var conditionalLcdEffects = new List<double>();
        var accessTargetEffects = new List<double>();
        var accessLcdEffects = new List<double>();
        foreach (IGrouping<string, AccessTraceRunCorrelation> pair in runs
                     .GroupBy(run => run.PairId, StringComparer.Ordinal))
        {
            AccessTraceRunCorrelation service = pair.Single(run => run.IsService);
            AccessTraceRunCorrelation control = pair.Single(run => !run.IsService);
            conditionalTargetEffects.Add(
                service.ConditionalLoopPower +
                service.ConditionalColorHarmonicPower -
                control.ConditionalLoopPower -
                control.ConditionalColorHarmonicPower);
            conditionalLcdEffects.Add(Math.Max(0,
                service.ConditionalLcdFramePower +
                service.ConditionalLcdEdgePower -
                control.ConditionalLcdFramePower -
                control.ConditionalLcdEdgePower));
            accessTargetEffects.Add(
                service.AccessRatePowers.LoopPower +
                service.AccessRatePowers.ColorHarmonicPower +
                service.AccessRatePowers.StateRatePower -
                control.AccessRatePowers.LoopPower -
                control.AccessRatePowers.ColorHarmonicPower -
                control.AccessRatePowers.StateRatePower);
            accessLcdEffects.Add(Math.Max(0,
                service.AccessRatePowers.LcdFramePower +
                service.AccessRatePowers.LcdEdgePower -
                control.AccessRatePowers.LcdFramePower -
                control.AccessRatePowers.LcdEdgePower));
        }
        return Median(conditionalTargetEffects) >
                2 * Median(conditionalLcdEffects) &&
            Median(accessTargetEffects) > 2 * Median(accessLcdEffects);
    }

    static double Ratio(double numerator, double denominator)
    {
        if (denominator > 0)
        {
            return numerator / denominator;
        }
        return numerator > 0 ? double.PositiveInfinity : 1;
    }

    static AccessTraceRotationResult RotationPValue(
        IReadOnlyList<AccessTraceCorrelationRunInput> runs,
        int nullCount)
    {
        LoopRotationScores[] scores = runs
            .OrderBy(run => run.PairId, StringComparer.Ordinal)
            .ThenBy(run => run.IsService)
            .Select(BuildLoopScores)
            .ToArray();
        double observedEffect = RotatedMatchedEffect(scores, iteration: -1);
        var exceedances = 0;
        double mean = 0;
        double sumSquaredDifferences = 0;
        for (var iteration = 0; iteration < nullCount; iteration++)
        {
            double nullEffect = RotatedMatchedEffect(scores, iteration);
            double difference = nullEffect - mean;
            mean += difference / (iteration + 1);
            sumSquaredDifferences += difference * (nullEffect - mean);
            if (nullEffect >= observedEffect)
            {
                exceedances++;
            }
        }
        double standardDeviation = nullCount <= 1
            ? 0
            : Math.Sqrt(sumSquaredDifferences / (nullCount - 1));
        double standardized = standardDeviation == 0
            ? observedEffect > mean ? double.PositiveInfinity : 0
            : (observedEffect - mean) / standardDeviation;
        return new(
            observedEffect,
            mean,
            standardDeviation,
            standardized,
            (exceedances + 1d) / (nullCount + 1d));
    }

    static LoopRotationScores BuildLoopScores(AccessTraceCorrelationRunInput run)
    {
        double[] frequencyGrid = AccessTraceFrequencies.LoopFrequencyGrid();
        var frequencyCounts = new long[frequencyGrid.Length][][][];
        for (var frequencyIndex = 0;
             frequencyIndex < frequencyGrid.Length;
             frequencyIndex++)
        {
            double frequency = frequencyGrid[frequencyIndex];
            bool[] frequencyIncluded = AccessTraceInclusion.Create(new(
                run.HeldValues.Length,
                run.DurationSeconds,
                2,
                AccessTraceWindowHalf.Full,
                frequency));
            frequencyIncluded = PrepareCategoricalMask(run, frequencyIncluded);
            string[] labels = AccessTraceCategorical.CreateLabels(
                run.HeldValues,
                frequencyIncluded);
            string[] categories = labels
                .Where((_, index) => frequencyIncluded[index])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var categoryIndexes = categories
                .Select((category, index) => (category, index))
                .ToDictionary(
                    item => item.category,
                    item => item.index,
                    StringComparer.Ordinal);
            const int firstLoop = 2;
            int loopCount = Math.Max(0, (int)Math.Floor(
                run.DurationSeconds * frequency) - firstLoop);
            long[][][] counts = Enumerable.Range(0, loopCount)
                .Select(_ => Enumerable.Range(
                        0,
                        AccessTraceFrequencies.PhaseCellCount)
                    .Select(_ => new long[categories.Length])
                    .ToArray())
                .ToArray();
            for (var index = 0; index < run.HeldValues.Length; index++)
            {
                if (!frequencyIncluded[index])
                {
                    continue;
                }
                double seconds = (index + 0.5) * run.DurationSeconds /
                    run.HeldValues.Length;
                int loop = (int)Math.Floor(seconds * frequency) - firstLoop;
                if ((uint)loop >= (uint)loopCount)
                {
                    continue;
                }
                double loopPhase = seconds * frequency;
                int phaseCell = (int)Math.Floor(
                    (loopPhase - Math.Floor(loopPhase)) *
                    AccessTraceFrequencies.PhaseCellCount);
                counts[loop][phaseCell][categoryIndexes[labels[index]]]++;
            }
            frequencyCounts[frequencyIndex] = counts;
        }
        return new(run.PairId, run.IsService, frequencyCounts);
    }

    static double RotatedMatchedEffect(
        LoopRotationScores[] runs,
        int iteration)
    {
        var runScores = new List<(string PairId, bool IsService, double Score)>();
        for (var runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            LoopRotationScores run = runs[runIndex];
            double association = 0;
            foreach (long[][][] counts in run.FrequencyPhaseCellCounts)
            {
                int loopCount = counts.Length;
                int categoryCount = loopCount == 0
                    ? 0
                    : counts[0][0].Length;
                long[][] phaseCells = Enumerable.Range(
                        0,
                        AccessTraceFrequencies.PhaseCellCount)
                    .Select(_ => new long[categoryCount])
                    .ToArray();
                for (var loop = 0; loop < loopCount; loop++)
                {
                    int shift = iteration < 0
                        ? 0
                        : SharedRotation(iteration, runIndex, loop);
                    for (var cell = 0;
                         cell < AccessTraceFrequencies.PhaseCellCount;
                         cell++)
                    {
                        int shiftedCell = (cell + shift) %
                            AccessTraceFrequencies.PhaseCellCount;
                        for (var category = 0;
                             category < categoryCount;
                             category++)
                        {
                            phaseCells[shiftedCell][category] +=
                                counts[loop][cell][category];
                        }
                    }
                }
                association = Math.Max(
                    association,
                    BestPhaseAssociation(phaseCells, categoryCount));
            }
            runScores.Add((
                run.PairId,
                run.IsService,
                association));
        }
        var effects = new List<double>();
        foreach (IGrouping<string, (string PairId, bool IsService, double Score)> pair
                 in runScores.GroupBy(item => item.PairId, StringComparer.Ordinal))
        {
            effects.Add(
                pair.Single(item => item.IsService).Score -
                pair.Single(item => !item.IsService).Score);
        }
        return Median(effects);
    }

    static double BestPhaseAssociation(
        long[][] phaseCells,
        int categoryCount)
    {
        double best = 0;
        for (var phase = 0;
             phase < AccessTraceFrequencies.FractionalPhaseSteps;
             phase++)
        {
            long[][] contingency = Enumerable.Range(0, 6)
                .Select(_ => new long[categoryCount])
                .ToArray();
            for (var cell = 0; cell < phaseCells.Length; cell++)
            {
                int shiftedCell = (cell + phase) %
                    AccessTraceFrequencies.PhaseCellCount;
                int state = shiftedCell * 6 /
                    AccessTraceFrequencies.PhaseCellCount;
                for (var category = 0; category < categoryCount; category++)
                {
                    contingency[state][category] += phaseCells[cell][category];
                }
            }
            best = Math.Max(best, AccessTraceCategorical.Association(contingency));
        }
        return best;
    }

    static int SharedRotation(int iteration, int runIndex, int loopIndex)
    {
        ulong value = (ulong)(iteration + 1) * 0x9e3779b97f4a7c15UL;
        value ^= (ulong)(runIndex + 1) * 0xbf58476d1ce4e5b9UL;
        value ^= (ulong)(loopIndex + 1) * 0x94d049bb133111ebUL;
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        value ^= value >> 31;
        return (int)(value % AccessTraceFrequencies.PhaseCellCount);
    }

    static void Validate(AccessTraceCorrelationCandidateInput candidate)
    {
        ValidateCandidateShape(candidate);
        foreach (AccessTraceCorrelationRunInput run in candidate.Runs)
        {
            ValidateRun(run);
        }
        ValidateMatchedPairs(candidate.Runs);
        ValidateMatchedDurations(candidate.Runs);
        ValidateCommonDuration(candidate.Runs);
    }

    static void ValidateCandidateShape(
        AccessTraceCorrelationCandidateInput candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.CandidateId) ||
            candidate.Runs.Count != 6)
        {
            throw new ArgumentException("A candidate needs three matched run pairs.");
        }
    }

    static void ValidateRun(AccessTraceCorrelationRunInput run)
    {
        if (string.IsNullOrWhiteSpace(run.PairId) ||
            !double.IsFinite(run.DurationSeconds) ||
            run.DurationSeconds <= 0 ||
            run.EventCounts.Length != AccessTraceWindow.RequiredBinCount ||
            run.TotalTraffic.Length != run.EventCounts.Length ||
            run.HeldValues.Length != run.EventCounts.Length)
        {
            throw new ArgumentException("Invalid correlation run input.");
        }
        ValidateRunCounts(run);
    }

    static void ValidateRunCounts(AccessTraceCorrelationRunInput run)
    {
        if (Enumerable.Range(0, run.EventCounts.Length).Any(index =>
                run.EventCounts[index] < 0 ||
                run.TotalTraffic[index] < 0 ||
                run.EventCounts[index] > run.TotalTraffic[index]))
        {
            throw new ArgumentException(
                "Run counts must be nonnegative and within total traffic.");
        }
    }

    static void ValidateMatchedPairs(
        IReadOnlyList<AccessTraceCorrelationRunInput> runs)
    {
        bool matched = runs
            .GroupBy(run => run.PairId, StringComparer.Ordinal)
            .Count(group => group.Count() == 2 &&
                group.Count(run => run.IsService) == 1) == 3;
        if (!matched)
        {
            throw new ArgumentException("The runs do not contain three matched pairs.");
        }
    }

    static void ValidateMatchedDurations(
        IReadOnlyList<AccessTraceCorrelationRunInput> runs)
    {
        foreach (IGrouping<string, AccessTraceCorrelationRunInput> pair in
                 runs.GroupBy(run => run.PairId, StringComparer.Ordinal))
        {
            double[] durations = pair.Select(run => run.DurationSeconds).ToArray();
            if (Math.Abs(durations[0] - durations[1]) > 1e-9)
            {
                throw new ArgumentException(
                    "Matched service and control durations must be equal.");
            }
        }
    }

    static void ValidateCommonDuration(
        IReadOnlyList<AccessTraceCorrelationRunInput> runs)
    {
        double referenceDuration = runs[0].DurationSeconds;
        if (runs.Any(run =>
                Math.Abs(run.DurationSeconds - referenceDuration) > 1e-9))
        {
            throw new ArgumentException(
                "All matched trace windows must have equal durations.");
        }
    }

    static double Median(IEnumerable<double> source)
    {
        double[] ordered = source.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

}
