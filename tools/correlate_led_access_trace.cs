#!/usr/bin/env dotnet
#:property AssemblyName=AvrCore.Tests
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using System.Text.Json;
using Mia.Emulator.Diagnostics;

const string DefaultTraceDirectory =
    "/tmp/mia-led-service-access-correlation";
const string VideoMeasurementsPath =
    "/tmp/mia-led-service-video-analysis/measurements.json";

try
{
    if (args.Length is < 1 or > 2 || args[0] is not (
            "--verify-inventory" or
            "--verify-differential" or
            "--verify-statistics" or
            "--diagnose" or
            "--verify-video" or
            "--verify"))
    {
        Console.Error.WriteLine(
            "Usage: dotnet run tools/correlate_led_access_trace.cs -- " +
            "--verify-inventory|--verify-differential|--verify-statistics|" +
            "--diagnose|" +
            "--verify-video|--verify [TRACE_DIRECTORY]");
        return 64;
    }

    string mode = args[0];
    bool needsTrace = mode != "--verify-video";
    bool needsVideo = mode is "--verify-video" or "--verify";
    if (needsVideo)
    {
        VerifyVideoMeasurements(VideoMeasurementsPath);
    }
    if (!needsTrace)
    {
        WriteSummary(
            mode,
            inventoryCount: 0,
            testedCount: 0,
            acceptedIds: [],
            nullCount: 0,
            placeboCount: 0);
        Console.WriteLine(
            $"ACCESS_TRACE_VIDEO_VERIFY PASS path={VideoMeasurementsPath}");
        return 0;
    }

    string traceDirectory = args.Length == 2
        ? args[1]
        : DefaultTraceDirectory;
    AccessTraceRunSnapshot[] snapshots = LoadMatchedRuns(traceDirectory);
    var inventoryIds = new HashSet<string>(StringComparer.Ordinal);
    var testedList = new List<AccessTraceCorrelationCandidateInput>();
    foreach (AccessTraceCorrelationCandidateInput candidate in
             AccessTraceCorrelationInputBuilder.Stream(snapshots))
    {
        inventoryIds.Add(candidate.CandidateId);
        if (PassesDifferentialPrefilter(candidate))
        {
            testedList.Add(candidate);
        }
    }
    AccessTraceCorrelationCandidateInput[] tested = [.. testedList];
    int inventoryCount = inventoryIds.Count;
    if (inventoryCount == 0)
    {
        throw new InvalidDataException("The trace inventory is empty.");
    }
    if (mode == "--verify-inventory")
    {
        WriteSummary(
            mode,
            inventoryCount,
            testedCount: 0,
            acceptedIds: [],
            nullCount: 0,
            placeboCount: 0);
        Console.WriteLine(
            $"ACCESS_TRACE_INVENTORY_VERIFY PASS runs=6 candidates={inventoryCount}");
        return 0;
    }
    if (tested.Length == 0)
    {
        throw new InvalidDataException(
            "No candidate passed the service-control prefilter.");
    }
    AccessTraceCorrelationReport report =
        AccessTraceCorrelationAnalyzer.Analyze(tested);
    if (mode == "--diagnose")
    {
        var ranked = report.Candidates
            .OrderBy(candidate => candidate.RotationPValue)
            .ThenByDescending(candidate => candidate.NullStandardizedEffect)
            .Take(30)
            .ToArray();
        foreach (var candidate in ranked)
        {
            Console.WriteLine(
                $"{candidate.CandidateId} q={candidate.AdjustedQValue:F4} " +
                $"p={candidate.RotationPValue:F5} z={candidate.NullStandardizedEffect:F2} " +
                $"effect={candidate.MedianServiceControlEffect:F3} " +
                $"allPos={candidate.AllPairEffectsPositive} " +
                $"ratio={candidate.MedianServiceControlRatio:F2} " +
                $"r2={candidate.MedianServiceR2:F3} " +
                $"phase={candidate.PhaseResultant:F3} " +
                $"offP={candidate.OffFrequencyPValue:F4} " +
                $"robust=[{string.Join(",", candidate.Robustness.Select(r => r.AllPairEffectsPositive ? "P" : "F"))}]");
        }
        Console.WriteLine($"DIAGNOSE_DONE tested={tested.Length}");
        return 0;
    }
    string[] accepted = report.Candidates
        .Where(candidate => candidate.Accepted)
        .Select(candidate => candidate.CandidateId)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (accepted.Length == 0)
    {
        throw new InvalidDataException(
            "No service-only periodic candidate passed global correction.");
    }
    WriteSummary(
        mode,
        inventoryCount,
        tested.Length,
        accepted,
        report.RotationNullCount,
        report.PlaceboFundamentalCount);
    string token = mode == "--verify-statistics"
        ? "ACCESS_TRACE_STATISTICS_VERIFY"
        : mode == "--verify-differential"
            ? "ACCESS_TRACE_DIFFERENTIAL_VERIFY"
            : "ACCESS_TRACE_VERIFY";
    Console.WriteLine(
        $"{token} PASS inventory={inventoryCount} tested={tested.Length} " +
        $"accepted={accepted.Length} nulls={report.RotationNullCount}");
    return 0;
}
catch (IOException exception)
{
    return WriteFailure(exception);
}
catch (UnauthorizedAccessException exception)
{
    return WriteFailure(exception);
}
catch (InvalidOperationException exception)
{
    return WriteFailure(exception);
}
catch (ArgumentException exception)
{
    return WriteFailure(exception);
}
catch (JsonException exception)
{
    return WriteFailure(exception);
}

static AccessTraceRunSnapshot[] LoadMatchedRuns(string directory)
{
    if (!Directory.Exists(directory))
    {
        throw new DirectoryNotFoundException(
            $"Trace directory does not exist: {directory}");
    }
    string[] files = Directory.EnumerateFiles(
            directory,
            "*",
            SearchOption.AllDirectories)
        .Where(IsAggregatePath)
        .Order(StringComparer.Ordinal)
        .ToArray();
    string[] service = files.Where(path => IsScenario(path, "service")).ToArray();
    string[] control = files.Where(path => IsScenario(path, "control")).ToArray();
    if (service.Length != 3 || control.Length != 3)
    {
        throw new InvalidDataException(
            $"Expected three service and three control aggregates; " +
            $"found service={service.Length}, control={control.Length}.");
    }

    var result = new List<AccessTraceRunSnapshot>(6);
    for (var index = 0; index < 3; index++)
    {
        string pairId = $"pair-{index + 1:00}";
        result.Add(new(pairId, false, AccessTraceJsonReader.Read(control[index])));
        result.Add(new(pairId, true, AccessTraceJsonReader.Read(service[index])));
    }
    return [.. result];
}

static bool IsAggregatePath(string path) =>
    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
    path.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase);

static bool IsScenario(string path, string scenario)
{
    string name = Path.GetFileName(path);
    return name.StartsWith(scenario, StringComparison.OrdinalIgnoreCase) ||
        name.Contains($"-{scenario}", StringComparison.OrdinalIgnoreCase);
}

static bool PassesDifferentialPrefilter(
    AccessTraceCorrelationCandidateInput candidate)
{
    if (!AccessTraceCorrelationInputBuilder.HasMatchedDifference(candidate))
    {
        return false;
    }
    var positivePairs = 0;
    foreach (IGrouping<string, AccessTraceCorrelationRunInput> pair in candidate.Runs
                 .GroupBy(run => run.PairId, StringComparer.Ordinal))
    {
        AccessTraceCorrelationRunInput service = pair.Single(run => run.IsService);
        AccessTraceCorrelationRunInput control = pair.Single(run => !run.IsService);
        double serviceAssociation = QuickAssociation(service);
        double controlAssociation = QuickAssociation(control);
        if (serviceAssociation - controlAssociation > 0.01)
        {
            positivePairs++;
        }
    }
    return positivePairs == 3;
}

static double QuickAssociation(AccessTraceCorrelationRunInput run)
{
    // Coarse pre-gate: downsample by 16 bins (the LED loop period is ~185
    // bins, so a 6-state phase remains ~31 downsampled bins wide) purely to
    // reject obviously aperiodic traffic cheaply. Surviving candidates get
    // the full-resolution analysis in the analyzer.
    const int Factor = 16;
    int downLength = run.HeldValues.Length / Factor;
    var held = new ulong?[downLength];
    var counts = new long[downLength];
    var traffic = new long[downLength];
    for (var index = 0; index < downLength; index++)
    {
        long total = 0;
        long trafficTotal = 0;
        ulong? value = null;
        for (var offset = 0; offset < Factor; offset++)
        {
            int source = index * Factor + offset;
            total += run.EventCounts[source];
            trafficTotal += run.TotalTraffic[source];
            if (!value.HasValue && source < run.HeldValues.Length)
            {
                value = run.HeldValues[source];
            }
        }
        counts[index] = total;
        traffic[index] = trafficTotal;
        held[index] = total > 0 || run.HeldValues[index * Factor].HasValue
            ? run.HeldValues[index * Factor]
            : null;
    }
    var downsampled = run with
    {
        EventCounts = counts,
        TotalTraffic = traffic,
        HeldValues = held,
    };
    bool[] included = AccessTraceInclusion.Create(
        downLength,
        run.DurationSeconds,
        excludedWholeLoops: 2,
        AccessTraceWindowHalf.Full);
    int firstAccess = Array.FindIndex(counts, count => count > 0);
    for (var index = 0; index < firstAccess; index++)
    {
        if (!run.HeldValues[index].HasValue)
        {
            included[index] = false;
        }
    }
    return AccessTraceCategorical.Fit(
        downsampled.HeldValues,
        run.DurationSeconds,
        included).Association;
}

static void VerifyVideoMeasurements(string path)
{
    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
    JsonElement cycle = document.RootElement.GetProperty("six_state_cycle");
    double frequency = cycle.GetProperty("cycle_frequency_hz").GetDouble();
    string[] order = cycle.GetProperty("phase_order")
        .EnumerateArray()
        .Select(item => item.GetString() ?? "")
        .ToArray();
    string[] expected =
        ["blue", "blue+red", "blue+green", "off", "red", "green"];
    if (Math.Abs(frequency - AccessTraceFrequencies.LoopHz) > 1e-9 ||
        !order.SequenceEqual(expected) ||
        cycle.GetProperty("complete_cycle_count").GetInt32() < 3)
    {
        throw new InvalidDataException(
            "The physical-video six-state measurement is invalid.");
    }
}

static void WriteSummary(
    string mode,
    int inventoryCount,
    int testedCount,
    IReadOnlyList<string> acceptedIds,
    int nullCount,
    int placeboCount)
{
    using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
    writer.WriteStartObject();
    writer.WriteString("schema", "mia-led-access-correlation-summary-v1");
    writer.WriteString("mode", mode);
    writer.WriteString("status", "PASS");
    writer.WriteNumber("inventoryCandidateCount", inventoryCount);
    writer.WriteNumber("testedCandidateCount", testedCount);
    writer.WriteNumber("rotationNullCount", nullCount);
    writer.WriteNumber("placeboFundamentalCount", placeboCount);
    writer.WriteStartArray("acceptedCandidateIds");
    foreach (string candidateId in acceptedIds)
    {
        writer.WriteStringValue(candidateId);
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
    writer.Flush();
    Console.WriteLine();
}

static int WriteFailure(Exception exception)
{
    Console.Error.WriteLine($"ACCESS_TRACE_VERIFY FAIL: {exception.Message}");
    return 1;
}
