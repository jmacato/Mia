// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed class AccessTraceAccumulator
{
    readonly AccessTraceWindow _window;
    readonly AccessTraceClockInfo _clock;
    readonly AccessTraceBusAccumulator _avr;
    readonly AccessTraceBusAccumulator _primaryI2c;
    readonly AccessTraceBusAccumulator _armMmio;

    public AccessTraceAccumulator(
        AccessTraceWindow window,
        AccessTraceClockInfo clock)
    {
        _window = window;
        _clock = clock;
        _avr = new(AccessTraceBus.Avr, window);
        _primaryI2c = new(AccessTraceBus.PrimaryI2c, window);
        _armMmio = new(AccessTraceBus.ArmMmio, window);
    }

    public int StoredAddressCount =>
        _avr.StoredAddressCount +
        _primaryI2c.StoredAddressCount +
        _armMmio.StoredAddressCount;

    public int StoredTimeBinCount =>
        _avr.StoredTimeBinCount +
        _primaryI2c.StoredTimeBinCount +
        _armMmio.StoredTimeBinCount;

    public int StoredBinValueCellCount =>
        _avr.StoredBinValueCellCount +
        _primaryI2c.StoredBinValueCellCount +
        _armMmio.StoredBinValueCellCount;

    public void RecordAvrRead(
        long cycle,
        int pc,
        int address,
        byte value) =>
        _avr.Record(new AccessTraceObservation(
            new(AccessTraceBus.Avr, checked((uint)address), 1),
            AccessTraceOperation.Read,
            cycle,
            value,
            checked((uint)pc),
            OldValue: null,
            Mask: null,
            BitWidth: 8));

    public void RecordAvrWrite(
        long cycle,
        int pc,
        int address,
        byte oldValue,
        byte newValue,
        byte mask,
        bool hookConsumed = false)
    {
        byte observedValue = hookConsumed
            ? newValue
            : (byte)((oldValue & ~mask) | (newValue & mask));
        _avr.Record(new AccessTraceObservation(
            new(AccessTraceBus.Avr, checked((uint)address), 1),
            AccessTraceOperation.Write,
            cycle,
            observedValue,
            checked((uint)pc),
            hookConsumed ? null : oldValue,
            mask,
            BitWidth: 8,
            ConsumedWrite: hookConsumed));
    }

    public void RecordPrimaryI2c(
        long cycle,
        int pc,
        bool isWrite,
        byte device,
        byte register,
        byte value,
        int payloadLength) =>
        _primaryI2c.Record(new AccessTraceObservation(
            new(
                AccessTraceBus.PrimaryI2c,
                PrimaryI2cTraceDecoder.DataAddress,
                Math.Max(1, payloadLength),
                device,
                register),
            isWrite ? AccessTraceOperation.Write : AccessTraceOperation.Read,
            cycle,
            value,
            checked((uint)pc),
            OldValue: null,
            Mask: null,
            BitWidth: 8));

    public void RecordArmMmio(ArmModemMmioAccess access) =>
        _armMmio.Record(new AccessTraceObservation(
            new(
                AccessTraceBus.ArmMmio,
                access.Address,
                access.Size),
            access.IsWrite ? AccessTraceOperation.Write : AccessTraceOperation.Read,
            access.Cycle,
            access.Value,
            access.Pc,
            OldValue: null,
            Mask: null,
            checked(access.Size * 8)));

    public AccessTraceSnapshot Snapshot()
    {
        AccessTraceAddressSnapshot[] avr = _avr.SnapshotAddresses();
        AccessTraceAddressSnapshot[] i2c = _primaryI2c.SnapshotAddresses();
        AccessTraceAddressSnapshot[] arm = _armMmio.SnapshotAddresses();
        AccessTraceAddressSnapshot[] addresses = [.. avr, .. i2c, .. arm];
        var reconciliation = new List<AccessTraceReconciliationSnapshot>(6);
        AddReconciliation(reconciliation, _avr, avr);
        AddReconciliation(reconciliation, _primaryI2c, i2c);
        AddReconciliation(reconciliation, _armMmio, arm);
        return new(
            AccessTraceSnapshot.CurrentSchema,
            _window,
            _clock,
            new(Stopped: false, StoppedAtCycle: null),
            CreateStorageMetrics(),
            reconciliation,
            addresses);
    }

    AccessTraceStorageMetrics CreateStorageMetrics() => new(
        StoredAddressCount,
        StoredTimeBinCount,
        StoredBinValueCellCount,
        _avr.StoredValueHistogramCellCount +
            _primaryI2c.StoredValueHistogramCellCount +
            _armMmio.StoredValueHistogramCellCount,
        _avr.StoredTransitionCellCount +
            _primaryI2c.StoredTransitionCellCount +
            _armMmio.StoredTransitionCellCount,
        _avr.StoredProgramCounterCellCount +
            _primaryI2c.StoredProgramCounterCellCount +
            _armMmio.StoredProgramCounterCellCount,
        _avr.BinValueOverflowCount +
            _primaryI2c.BinValueOverflowCount +
            _armMmio.BinValueOverflowCount,
        _avr.ValueOverflowCount +
            _primaryI2c.ValueOverflowCount +
            _armMmio.ValueOverflowCount,
        _avr.TransitionOverflowCount +
            _primaryI2c.TransitionOverflowCount +
            _armMmio.TransitionOverflowCount,
        _avr.ProgramCounterOverflowCount +
            _primaryI2c.ProgramCounterOverflowCount +
            _armMmio.ProgramCounterOverflowCount);

    static void AddReconciliation(
        List<AccessTraceReconciliationSnapshot> destination,
        AccessTraceBusAccumulator source,
        IReadOnlyList<AccessTraceAddressSnapshot> addresses)
    {
        destination.Add(source.Reconcile(AccessTraceOperation.Read, addresses));
        destination.Add(source.Reconcile(AccessTraceOperation.Write, addresses));
    }
}
