// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceCorrelationInputBuilder
{
    public static IReadOnlyList<AccessTraceCorrelationCandidateInput> Build(
        IReadOnlyList<AccessTraceRunSnapshot> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count != 6)
        {
            throw new ArgumentException(
                "Three matched service and control pairs are required.",
                nameof(runs));
        }
        AccessTraceRunIndex[] indexes = runs.Select(CreateIndex).ToArray();
        string[] inventory = indexes
            .SelectMany(index => index.Operations.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var metadata = indexes
            .SelectMany(index => index.Operations)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (group.First().Value.Bus, group.First().Value.Operation),
                StringComparer.Ordinal);
        return inventory
            .Select(candidateId => new AccessTraceCorrelationCandidateInput(
            candidateId,
            indexes.Select(index => CreateRunInput(
                index,
                candidateId,
                metadata[candidateId])).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Streams candidates lazily so a prefilter can discard most of them
    /// before their full-resolution arrays are materialized. Each yielded
    /// candidate is independent; the caller must not retain the builder.
    /// </summary>
    public static IEnumerable<AccessTraceCorrelationCandidateInput> Stream(
        IReadOnlyList<AccessTraceRunSnapshot> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count != 6)
        {
            throw new ArgumentException(
                "Three matched service and control pairs are required.",
                nameof(runs));
        }
        AccessTraceRunIndex[] indexes = null!;
        Dictionary<string, (AccessTraceBus Bus, AccessTraceOperation Operation)>
            metadata = null!;
        string[] inventoryIds = null!;
        foreach (var _ in new[] { true })
        {
            indexes ??= runs.Select(CreateIndex).ToArray();
            inventoryIds ??= indexes
                .SelectMany(index => index.Operations.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            metadata ??= indexes
                .SelectMany(index => index.Operations)
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (group.First().Value.Bus, group.First().Value.Operation),
                    StringComparer.Ordinal);
        }
        foreach (string candidateId in inventoryIds)
        {
            yield return new(
                candidateId,
                indexes.Select(index => CreateRunInput(
                    index,
                    candidateId,
                    metadata[candidateId])).ToArray());
        }
    }

    public static bool HasMatchedDifference(
        AccessTraceCorrelationCandidateInput candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        foreach (IGrouping<string, AccessTraceCorrelationRunInput> pair in candidate.Runs
                     .GroupBy(run => run.PairId, StringComparer.Ordinal))
        {
            AccessTraceCorrelationRunInput service = pair.Single(run => run.IsService);
            AccessTraceCorrelationRunInput control = pair.Single(run => !run.IsService);
            if (!service.EventCounts.SequenceEqual(control.EventCounts) ||
                !service.HeldValues.SequenceEqual(control.HeldValues))
            {
                return true;
            }
        }
        return false;
    }

    public static string FormatCandidateId(
        AccessTraceAddressKey key,
        AccessTraceOperation operation) =>
        $"{AccessTraceJson.Format(key.Bus)}:{AccessTraceJson.Format(operation)}:" +
        $"0x{key.Address:x}:size={key.Size}:device=" +
        $"{FormatNullableByte(key.Device)}:register={FormatNullableByte(key.Register)}";

    static AccessTraceRunIndex CreateIndex(AccessTraceRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run.Snapshot);
        if (run.Snapshot.Window.BinCount != AccessTraceWindow.RequiredBinCount ||
            run.Snapshot.Clock.MachineCyclesPerSecond <= 0 ||
            !run.Snapshot.Capture.Stopped ||
            run.Snapshot.Capture.StoppedAtCycle !=
                run.Snapshot.Window.EndCycleExclusive)
        {
            throw new InvalidDataException("Invalid aggregate clock or bin count.");
        }
        foreach (AccessTraceReconciliationSnapshot item in run.Snapshot.Reconciliation)
        {
            if (item.CallbackCount !=
                    item.AggregatedCount + item.OutsideWindowCount ||
                item.OutsideWindowCount != 0)
            {
                throw new InvalidDataException(
                    "The aggregate callback reconciliation failed.");
            }
        }
        var operations = new Dictionary<
            string,
            (AccessTraceBus Bus,
                AccessTraceOperation Operation,
                AccessTraceOperationSnapshot Snapshot)>(StringComparer.Ordinal);
        var totals = new Dictionary<(AccessTraceBus, AccessTraceOperation), long[]>();
        foreach (AccessTraceAddressSnapshot address in run.Snapshot.Addresses)
        {
            foreach (AccessTraceOperationSnapshot operation in address.Operations)
            {
                string id = FormatCandidateId(address.Key, operation.Operation);
                operations.Add(id, (address.Key.Bus, operation.Operation, operation));
                var totalKey = (address.Key.Bus, operation.Operation);
                if (!totals.TryGetValue(totalKey, out long[]? total))
                {
                    total = new long[AccessTraceWindow.RequiredBinCount];
                    totals.Add(totalKey, total);
                }
                long[] events = AccessTraceSeriesBuilder.ExpandEventCounts(operation);
                for (var index = 0; index < total.Length; index++)
                {
                    total[index] = checked(total[index] + events[index]);
                }
            }
        }
        return new(run.PairId, run.IsService, run.Snapshot, operations, totals);
    }

    static AccessTraceCorrelationRunInput CreateRunInput(
        AccessTraceRunIndex run,
        string candidateId,
        (AccessTraceBus Bus, AccessTraceOperation Operation) metadata)
    {
        if (!run.Operations.TryGetValue(candidateId, out var item))
        {
            return new(
                run.PairId,
                run.IsService,
                GetDurationSeconds(run.Snapshot),
                new long[AccessTraceWindow.RequiredBinCount],
                run.Totals.TryGetValue(metadata, out long[]? total)
                    ? (long[])total.Clone()
                    : new long[AccessTraceWindow.RequiredBinCount],
                new ulong?[AccessTraceWindow.RequiredBinCount]);
        }
        return new(
            run.PairId,
            run.IsService,
            GetDurationSeconds(run.Snapshot),
            AccessTraceSeriesBuilder.ExpandEventCounts(item.Snapshot),
            (long[])run.Totals[(item.Bus, item.Operation)].Clone(),
            AccessTraceSeriesBuilder.ExpandHeldValues(item.Snapshot));
    }

    static double GetDurationSeconds(AccessTraceSnapshot snapshot) =>
        (double)snapshot.Window.DurationCycles /
        snapshot.Clock.MachineCyclesPerSecond;

    static string FormatNullableByte(byte? value) => value.HasValue
        ? $"0x{value.Value:x2}"
        : "none";

}
