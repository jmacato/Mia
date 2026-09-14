// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed class AccessTraceMutableOperation
{
    readonly AccessTraceOperation _operation;
    readonly int _bitWidth;
    readonly Dictionary<ulong, long> _values = [];
    readonly Dictionary<(ulong OldValue, ulong NewValue), long> _transitions = [];
    readonly Dictionary<ulong, long> _programCounters = [];
    readonly Dictionary<ulong, long> _masks = [];
    readonly Dictionary<int, AccessTraceMutableTimeBin> _timeBins = [];
    readonly long[] _rises;
    readonly long[] _falls;
    readonly int _valueCellLimit;
    readonly int _transitionCellLimit;
    readonly int _binValueCellLimit;
    readonly int _programCounterCellLimit;
    bool _hasPreviousValue;
    ulong _previousValue;
    long _valueOverflowCount;
    long _transitionOverflowCount;
    long _programCounterOverflowCount;
    long _maskOverflowCount;

    public AccessTraceMutableOperation(
        AccessTraceOperation operation,
        int bitWidth)
    {
        if (bitWidth is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth));
        }

        _operation = operation;
        _bitWidth = bitWidth;
        _valueCellLimit = bitWidth <= 8
            ? AccessTraceLimits.ByteValueCells
            : AccessTraceLimits.WideValueCells;
        _transitionCellLimit = bitWidth <= 8
            ? AccessTraceLimits.ByteTransitionCells
            : AccessTraceLimits.WideTransitionCells;
        _binValueCellLimit = bitWidth <= 8
            ? AccessTraceLimits.ByteValueCells
            : AccessTraceLimits.WideBinValueCells;
        _programCounterCellLimit = bitWidth <= 8
            ? AccessTraceLimits.AvrProgramCounterCells
            : AccessTraceLimits.ProgramCounterCells;
        _rises = new long[bitWidth];
        _falls = new long[bitWidth];
    }

    public long Count { get; private set; }

    public long ChangeCount { get; private set; }

    public long ConsumedWriteCount { get; private set; }

    public long FirstCycle { get; private set; } = long.MaxValue;

    public long LastCycle { get; private set; } = long.MinValue;

    public ulong? InitialHeldValue { get; private set; }

    public int StoredTimeBinCount => _timeBins.Count;

    public int StoredBinValueCellCount =>
        _timeBins.Values.Sum(bin => bin.ValueCellCount);

    public int StoredValueHistogramCellCount => _values.Count;

    public int StoredTransitionCellCount => _transitions.Count;

    public int StoredProgramCounterCellCount => _programCounters.Count;

    public long BinValueOverflowCount =>
        _timeBins.Values.Sum(bin => bin.ValueOverflowCount);

    public long ValueOverflowCount => _valueOverflowCount;

    public long TransitionOverflowCount => _transitionOverflowCount;

    public long ProgramCounterOverflowCount => _programCounterOverflowCount;

    public void Record(AccessTraceObservation observation, int binIndex)
    {
        ValidateCycle(observation.Cycle);
        RecordOccurrence(observation);
        RecordObservedMask(observation.Mask);
        RecordConsumedWrite(observation.ConsumedWrite);
        RecordInitialHeldValue(observation.OldValue);

        bool changed = RecordTransition(
            observation.Value,
            observation.OldValue,
            observation.Mask);

        _previousValue = observation.Value;
        _hasPreviousValue = true;
        GetOrCreateTimeBin(binIndex).Record(observation.Value, changed);
    }

    void ValidateCycle(long cycle)
    {
        if (cycle < LastCycle)
        {
            throw new InvalidOperationException(
                "Access cycles for one bus address and operation must be monotonic.");
        }
    }

    void RecordOccurrence(AccessTraceObservation observation)
    {
        FirstCycle = Math.Min(FirstCycle, observation.Cycle);
        LastCycle = observation.Cycle;
        Count++;
        AddBounded(
            _values,
            observation.Value,
            _valueCellLimit,
            ref _valueOverflowCount);
        AddBounded(
            _programCounters,
            observation.InstructionAddress,
            _programCounterCellLimit,
            ref _programCounterOverflowCount);
    }

    void RecordObservedMask(ulong? mask)
    {
        if (mask is ulong observedMask)
        {
            AddBounded(
                _masks,
                observedMask,
                AccessTraceLimits.MaskCells,
                ref _maskOverflowCount);
        }
    }

    void RecordConsumedWrite(bool consumedWrite)
    {
        if (consumedWrite)
        {
            ConsumedWriteCount++;
        }
    }

    void RecordInitialHeldValue(ulong? oldValue)
    {
        if (Count == 1 && oldValue is ulong initialValue)
        {
            InitialHeldValue = initialValue;
        }
    }

    public AccessTraceOperationSnapshot Snapshot() => new(
        _operation,
        Count,
        ChangeCount,
        ConsumedWriteCount,
        FirstCycle,
        LastCycle,
        InitialHeldValue,
        _bitWidth,
        _valueOverflowCount,
        _transitionOverflowCount,
        _programCounterOverflowCount,
        _maskOverflowCount,
        BinValueOverflowCount,
        ToCounts(_values),
        _transitions
            .OrderBy(pair => pair.Key.OldValue)
            .ThenBy(pair => pair.Key.NewValue)
            .Select(pair => new AccessTraceTransitionCount(
                pair.Key.OldValue,
                pair.Key.NewValue,
                pair.Value))
            .ToArray(),
        Enumerable.Range(0, _bitWidth)
            .Select(bit => new AccessTraceBitEdgeCount(
                bit,
                _rises[bit],
                _falls[bit]))
            .ToArray(),
        _programCounters
            .OrderBy(pair => pair.Key)
            .Select(pair => new AccessTracePcCount(pair.Key, pair.Value))
            .ToArray(),
        ToCounts(_masks),
        _timeBins
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .ToArray());

    static void AddBounded(
        Dictionary<ulong, long> counts,
        ulong key,
        int limit,
        ref long overflowCount)
    {
        if (IncrementExistingCount(counts, key) ||
            AddNewCount(counts, key, limit))
        {
            return;
        }
        overflowCount++;
    }

    static bool IncrementExistingCount(
        Dictionary<ulong, long> counts,
        ulong key)
    {
        if (!counts.TryGetValue(key, out long count))
        {
            return false;
        }
        counts[key] = count + 1;
        return true;
    }

    static bool AddNewCount(
        Dictionary<ulong, long> counts,
        ulong key,
        int limit)
    {
        if (counts.Count >= limit)
        {
            return false;
        }
        counts.Add(key, 1);
        return true;
    }

    bool RecordTransition(
        ulong newValue,
        ulong? explicitOldValue,
        ulong? mask)
    {
        if (!explicitOldValue.HasValue && !_hasPreviousValue)
        {
            return false;
        }

        ulong oldValue = explicitOldValue ?? _previousValue;
        AddTransition(oldValue, newValue);
        ulong widthMask = _bitWidth == 64
            ? ulong.MaxValue
            : (1UL << _bitWidth) - 1;
        ulong changedBits = (oldValue ^ newValue) & widthMask &
            (mask ?? widthMask);
        return RecordChangedBits(oldValue, newValue, changedBits);
    }

    void AddTransition(ulong oldValue, ulong newValue)
    {
        var transition = (oldValue, newValue);
        bool exists = _transitions.TryGetValue(transition, out long count);
        switch (exists, _transitions.Count < _transitionCellLimit)
        {
            case (true, _):
                _transitions[transition] = count + 1;
                break;
            case (false, true):
                _transitions.Add(transition, 1);
                break;
            default:
                _transitionOverflowCount++;
                break;
        }
    }

    bool RecordChangedBits(
        ulong oldValue,
        ulong newValue,
        ulong changedBits)
    {
        if (changedBits == 0)
        {
            return false;
        }

        ChangeCount++;
        RecordBitEdges(oldValue, newValue, changedBits);
        return true;
    }

    AccessTraceMutableTimeBin GetOrCreateTimeBin(int binIndex)
    {
        if (_timeBins.TryGetValue(binIndex, out var bin))
        {
            return bin;
        }

        bin = new AccessTraceMutableTimeBin(_binValueCellLimit);
        _timeBins.Add(binIndex, bin);
        return bin;
    }

    void RecordBitEdges(ulong oldValue, ulong newValue, ulong changedBits)
    {
        for (var bit = 0; bit < _bitWidth; bit++)
        {
            ulong bitMask = 1UL << bit;
            if ((changedBits & bitMask) == 0)
            {
                continue;
            }
            if ((newValue & bitMask) != 0)
            {
                _rises[bit]++;
            }
            else
            {
                _falls[bit]++;
            }
        }
    }

    static AccessTraceCount[] ToCounts(Dictionary<ulong, long> counts) =>
        counts
            .OrderBy(pair => pair.Key)
            .Select(pair => new AccessTraceCount(pair.Key, pair.Value))
            .ToArray();
}
