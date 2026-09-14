// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed class AccessTraceMutableAddress
{
    readonly AccessTraceAddressKey _key;
    readonly int _bitWidth;
    readonly Dictionary<AccessTraceOperation, AccessTraceMutableOperation>
        _operations = [];

    public AccessTraceMutableAddress(
        AccessTraceAddressKey key,
        int bitWidth)
    {
        _key = key;
        _bitWidth = bitWidth;
    }

    public int StoredTimeBinCount =>
        _operations.Values.Sum(operation => operation.StoredTimeBinCount);

    public int StoredBinValueCellCount =>
        _operations.Values.Sum(operation => operation.StoredBinValueCellCount);

    public int StoredValueHistogramCellCount =>
        _operations.Values.Sum(
            operation => operation.StoredValueHistogramCellCount);

    public int StoredTransitionCellCount =>
        _operations.Values.Sum(operation => operation.StoredTransitionCellCount);

    public int StoredProgramCounterCellCount =>
        _operations.Values.Sum(
            operation => operation.StoredProgramCounterCellCount);

    public long BinValueOverflowCount =>
        _operations.Values.Sum(operation => operation.BinValueOverflowCount);

    public long ValueOverflowCount =>
        _operations.Values.Sum(operation => operation.ValueOverflowCount);

    public long TransitionOverflowCount =>
        _operations.Values.Sum(operation => operation.TransitionOverflowCount);

    public long ProgramCounterOverflowCount =>
        _operations.Values.Sum(operation => operation.ProgramCounterOverflowCount);

    public void Record(AccessTraceObservation observation, int binIndex)
    {
        if (!_operations.TryGetValue(
                observation.Operation,
                out AccessTraceMutableOperation? aggregate))
        {
            aggregate = new AccessTraceMutableOperation(
                observation.Operation,
                _bitWidth);
            _operations.Add(observation.Operation, aggregate);
        }
        aggregate.Record(observation, binIndex);
    }

    public AccessTraceAddressSnapshot Snapshot()
    {
        AccessTraceOperationSnapshot[] operations = _operations
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Snapshot())
            .ToArray();
        return new(
            _key,
            operations.Sum(operation => operation.Count),
            operations
                .Where(operation => operation.Operation == AccessTraceOperation.Read)
                .Sum(operation => operation.Count),
            operations
                .Where(operation => operation.Operation == AccessTraceOperation.Write)
                .Sum(operation => operation.Count),
            operations.Sum(operation => operation.ChangeCount),
            operations.Min(operation => operation.FirstCycle),
            operations.Max(operation => operation.LastCycle),
            operations);
    }
}
