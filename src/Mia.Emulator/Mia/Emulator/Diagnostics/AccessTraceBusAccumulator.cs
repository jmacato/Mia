// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

/// <summary>
/// Mutable aggregate owned by one emulated bus thread. AVR and primary I2C
/// each run on the AVR owner; ARM MMIO runs on the ARM owner. A snapshot is
/// taken only after the machine work item has made both owners quiescent.
/// </summary>
internal sealed class AccessTraceBusAccumulator
{
    readonly AccessTraceBus _bus;
    readonly AccessTraceWindow _window;
    readonly Dictionary<AccessTraceAddressKey, AccessTraceMutableAddress>
        _addresses = [];
    readonly long[] _callbackCounts = new long[2];
    readonly long[] _outsideWindowCounts = new long[2];

    public AccessTraceBusAccumulator(
        AccessTraceBus bus,
        AccessTraceWindow window)
    {
        _bus = bus;
        _window = window;
    }

    public int StoredAddressCount => _addresses.Count;

    public int StoredTimeBinCount =>
        _addresses.Values.Sum(address => address.StoredTimeBinCount);

    public int StoredBinValueCellCount =>
        _addresses.Values.Sum(address => address.StoredBinValueCellCount);

    public int StoredValueHistogramCellCount =>
        _addresses.Values.Sum(address => address.StoredValueHistogramCellCount);

    public int StoredTransitionCellCount =>
        _addresses.Values.Sum(address => address.StoredTransitionCellCount);

    public int StoredProgramCounterCellCount =>
        _addresses.Values.Sum(address => address.StoredProgramCounterCellCount);

    public long BinValueOverflowCount =>
        _addresses.Values.Sum(address => address.BinValueOverflowCount);

    public long ValueOverflowCount =>
        _addresses.Values.Sum(address => address.ValueOverflowCount);

    public long TransitionOverflowCount =>
        _addresses.Values.Sum(address => address.TransitionOverflowCount);

    public long ProgramCounterOverflowCount =>
        _addresses.Values.Sum(address => address.ProgramCounterOverflowCount);

    public void Record(AccessTraceObservation observation)
    {
        if (observation.Key.Bus != _bus)
        {
            throw new ArgumentException(
                "The address belongs to a different bus aggregate.",
                nameof(observation));
        }

        int operationIndex = (int)observation.Operation;
        _callbackCounts[operationIndex]++;
        if (!_window.Contains(observation.Cycle))
        {
            _outsideWindowCounts[operationIndex]++;
            return;
        }

        if (!_addresses.TryGetValue(
                observation.Key,
                out AccessTraceMutableAddress? address))
        {
            address = new AccessTraceMutableAddress(
                observation.Key,
                observation.BitWidth);
            _addresses.Add(observation.Key, address);
        }
        address.Record(
            observation,
            _window.GetBinIndex(observation.Cycle));
    }

    public AccessTraceAddressSnapshot[] SnapshotAddresses() => _addresses
        .OrderBy(pair => pair.Key.Address)
        .ThenBy(pair => pair.Key.Size)
        .ThenBy(pair => pair.Key.Device)
        .ThenBy(pair => pair.Key.Register)
        .Select(pair => pair.Value.Snapshot())
        .ToArray();

    public AccessTraceReconciliationSnapshot Reconcile(
        AccessTraceOperation operation,
        IReadOnlyList<AccessTraceAddressSnapshot> addresses)
    {
        long aggregateCount = addresses
            .SelectMany(address => address.Operations)
            .Where(item => item.Operation == operation)
            .Sum(item => item.Count);
        long callbackCount = _callbackCounts[(int)operation];
        long outsideWindowCount = _outsideWindowCounts[(int)operation];
        if (aggregateCount + outsideWindowCount != callbackCount)
        {
            throw new InvalidOperationException(
                $"{_bus} {operation} callback reconciliation failed: " +
                $"callbacks={callbackCount}, aggregates={aggregateCount}, " +
                $"outside={outsideWindowCount}.");
        }
        return new(
            _bus,
            operation,
            callbackCount,
            aggregateCount,
            outsideWindowCount);
    }
}
