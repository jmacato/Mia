// SPDX-License-Identifier: MIT

using Mia.Emulator.Diagnostics;
using Xunit;

namespace AvrCore.Tests;

public sealed class AccessTraceCorrelationTests
{
    const int BinCount = AccessTraceWindow.RequiredBinCount;
    static readonly ulong[] ArbitrarySixStateValues =
        [0x40, 0x03, 0xa7, 0x00, 0x80, 0x12];
    static readonly long[] SixStateEventCounts = [12, 3, 9, 2, 7, 4];
    static readonly string[] ExpectedInventory =
        ["lcd-only", "scheduler-only", "service-source", "shared-control"];

    [Fact]
    public void AccessTraceCorrelationRecoversServiceSourceAndRejectsAdversaries()
    {
        AccessTraceCorrelationCandidateInput service = CreateCandidate(
            "service-source",
            SyntheticKind.ServiceSource);
        AccessTraceCorrelationCandidateInput shared = CreateCandidate(
            "shared-control",
            SyntheticKind.SharedControl);
        AccessTraceCorrelationCandidateInput scheduler = CreateCandidate(
            "scheduler-only",
            SyntheticKind.SchedulerOnly);
        AccessTraceCorrelationCandidateInput lcd = CreateCandidate(
            "lcd-only",
            SyntheticKind.LcdOnly);

        AccessTraceCorrelationReport report = AccessTraceCorrelationAnalyzer.Analyze(
            [scheduler, service, lcd, shared]);

        Assert.Equal(
            ExpectedInventory,
            report.InventoriedCandidateIds);
        Assert.Equal(AccessTraceFrequencies.MinimumPlaceboFundamentals,
            report.PlaceboFundamentalCount);
        Assert.True(report.RotationNullCount >= 9_999);
        AccessTraceCandidateCorrelation recovered = Assert.Single(
            report.Candidates,
            candidate => candidate.CandidateId == "service-source");
        Assert.True(recovered.Accepted);
        Assert.True(recovered.AllPairEffectsPositive);
        Assert.True(recovered.MedianServiceControlEffect >= 0.05);
        Assert.True(recovered.MedianServiceControlRatio >= 2);
        Assert.True(recovered.MedianServiceR2 >= 0.02);
        Assert.True(recovered.PhaseResultant >= 0.70);
        Assert.True(recovered.NullStandardizedEffect >= 3);
        Assert.True(recovered.AdjustedQValue <= 0.05);
        Assert.All(recovered.Robustness, result =>
        {
            Assert.True(result.AllPairEffectsPositive);
            Assert.True(result.RelativeEffectChange <= 0.25);
            Assert.True(result.TargetPowersPass);
            Assert.True(result.ValuePhasePass);
        });
        Assert.All(
            recovered.Runs.Where(run => run.IsService),
            run =>
            {
                Assert.True(run.AccessRatePowers.LoopPower > 0);
                Assert.True(run.AccessRatePowers.StateRatePower > 0);
                Assert.True(run.ConditionalLoopPower > 0);
                Assert.Contains(
                    run.StateValueCounts,
                    item => item.ValueLabel == "0xa7" && item.Count > 0);
            });
        Assert.Null(recovered.AnchoredStateNames);
        Assert.All(
            recovered.PlaceboFrequenciesHz,
            frequency => Assert.True(
                frequency * 2 < BinCount /
                    (2 * (12 / AccessTraceFrequencies.LoopHz))));
        Assert.False(report.Candidates.Single(
            candidate => candidate.CandidateId == "shared-control").Accepted);
        Assert.False(report.Candidates.Single(
            candidate => candidate.CandidateId == "scheduler-only").Accepted);
        Assert.False(report.Candidates.Single(
            candidate => candidate.CandidateId == "lcd-only").Accepted);
    }

    [Fact]
    public void AccessTraceCorrelationPreservesMissingExposureAndValidatesRuns()
    {
        double[] normalized = AccessTraceSpectral.NormalizeByTotalTraffic(
            new long[] { 0, 1, 0, 1, 0 },
            new long[] { 0, 2, 0, 2, 0 });
        Assert.True(double.IsNaN(normalized[0]));
        Assert.Equal(0.5, normalized[1]);
        AccessTraceFrequencyFit fit = AccessTraceSpectral.Fit(
            normalized,
            durationSeconds: 1,
            frequencyHz: 1);
        Assert.Equal(2, fit.IncludedBinCount);

        AccessTraceCorrelationCandidateInput invalid = CreateCandidate(
            "invalid",
            SyntheticKind.ServiceSource);
        invalid.Runs[0].EventCounts[10] = 101;
        Assert.Throws<ArgumentException>(() =>
            AccessTraceCorrelationAnalyzer.Analyze([invalid]));
    }

    [Fact]
    public void AccessTraceCorrelationBenjaminiHochbergIsGlobalAndMonotone()
    {
        IReadOnlyDictionary<string, double> adjusted =
            AccessTraceMultipleTesting.BenjaminiHochberg(
            [
                ("c", 0.03),
                ("a", 0.001),
                ("b", 0.02),
            ]);

        Assert.Equal(0.003, adjusted["a"], 12);
        Assert.Equal(0.03, adjusted["b"], 12);
        Assert.Equal(0.03, adjusted["c"], 12);
    }

    [Fact]
    public void AccessTraceCorrelationInventoriesUnionBeforeFiltering()
    {
        var runs = new List<AccessTraceRunSnapshot>();
        for (var pair = 0; pair < 3; pair++)
        {
            AccessTraceAccumulator control = CreateAccumulator();
            control.RecordAvrWrite(10, 4, 0x40, 0, 1, 0xff);
            AccessTraceAccumulator service = CreateAccumulator();
            service.RecordAvrWrite(10, 4, 0x40, 0, 2, 0xff);
            service.RecordAvrRead(11, 5, 0x41, 3);
            runs.Add(new($"pair-{pair}", false, Stopped(control.Snapshot())));
            runs.Add(new($"pair-{pair}", true, Stopped(service.Snapshot())));
        }

        IReadOnlyList<AccessTraceCorrelationCandidateInput> inventory =
            AccessTraceCorrelationInputBuilder.Build(runs);

        Assert.Equal(2, inventory.Count);
        Assert.Contains(inventory,
            item => item.CandidateId.Contains("0x40", StringComparison.Ordinal));
        Assert.Contains(inventory,
            item => item.CandidateId.Contains("0x41", StringComparison.Ordinal));
        Assert.All(inventory, candidate => Assert.Equal(6, candidate.Runs.Count));
        Assert.All(inventory,
            candidate => Assert.True(
                AccessTraceCorrelationInputBuilder.HasMatchedDifference(candidate)));
    }

    static AccessTraceCorrelationCandidateInput CreateCandidate(
        string id,
        SyntheticKind kind)
    {
        var runs = new List<AccessTraceCorrelationRunInput>();
        for (var pair = 0; pair < 3; pair++)
        {
            runs.Add(CreateRun(pair, isService: false, kind));
            runs.Add(CreateRun(pair, isService: true, kind));
        }
        return new(id, runs);
    }

    static AccessTraceCorrelationRunInput CreateRun(
        int pair,
        bool isService,
        SyntheticKind kind)
    {
        const int loopCount = 12;
        double duration = loopCount / AccessTraceFrequencies.LoopHz;
        var events = new long[BinCount];
        var traffic = Enumerable.Repeat(100L, BinCount).ToArray();
        var held = new ulong?[BinCount];
        double fractionalPhase = 0.037 + pair * 0.0002;
        for (var index = 0; index < BinCount; index++)
        {
            double seconds = (index + 0.5) * duration / BinCount;
            int state = (int)Math.Floor(
                (seconds * AccessTraceFrequencies.LoopHz + fractionalPhase) * 6) % 6;
            (held[index], events[index]) = CreateSyntheticSample(
                kind,
                isService,
                state,
                seconds);
        }
        return new($"pair-{pair}", isService, duration, events, traffic, held);
    }

    static (ulong Held, long Events) CreateSyntheticSample(
        SyntheticKind kind,
        bool isService,
        int state,
        double seconds) => (kind, isService) switch
        {
            (SyntheticKind.ServiceSource, true) or
            (SyntheticKind.SharedControl, _) =>
                (ArbitrarySixStateValues[state], SixStateEventCounts[state]),
            (SyntheticKind.SchedulerOnly, true) =>
                (0x55, state == 0 ? 12 : 2),
            (SyntheticKind.LcdOnly, true) => Math.Sin(
                2 * Math.PI * AccessTraceFrequencies.LcdFrameHz * seconds) >= 0
                    ? (0x71UL, 10L)
                    : (0x19UL, 2L),
            _ => (0x55, 2),
        };

    static AccessTraceAccumulator CreateAccumulator() => new(
        new(0, 13_000_000),
        new("asic-13mhz", 13_000_000, 12_000_000, 13, 12, 0));

    static AccessTraceSnapshot Stopped(AccessTraceSnapshot snapshot) =>
        snapshot with { Capture = new(true, snapshot.Window.EndCycleExclusive) };

}
