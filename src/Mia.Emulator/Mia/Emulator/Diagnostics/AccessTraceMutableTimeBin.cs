// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed class AccessTraceMutableTimeBin
{
    readonly int _valueCellLimit;
    readonly Dictionary<ulong, long> _values = [];

    public AccessTraceMutableTimeBin(int valueCellLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCellLimit);
        _valueCellLimit = valueCellLimit;
    }

    public long EventCount { get; private set; }

    public long ChangeCount { get; private set; }

    public ulong LastValue { get; private set; }

    public int ValueCellCount => _values.Count;

    public long ValueOverflowCount { get; private set; }

    public void Record(ulong value, bool changed)
    {
        EventCount++;
        RecordChange(changed);
        LastValue = value;
        if (IncrementExistingValue(value) || AddNewValue(value))
        {
            return;
        }
        ValueOverflowCount++;
    }

    void RecordChange(bool changed)
    {
        if (changed)
        {
            ChangeCount++;
        }
    }

    bool IncrementExistingValue(ulong value)
    {
        if (!_values.TryGetValue(value, out long count))
        {
            return false;
        }
        _values[value] = count + 1;
        return true;
    }

    bool AddNewValue(ulong value)
    {
        if (_values.Count >= _valueCellLimit)
        {
            return false;
        }
        _values.Add(value, 1);
        return true;
    }

    public AccessTraceTimeBinSnapshot Snapshot(int index) => new(
        index,
        EventCount,
        ChangeCount,
        LastValue,
        ValueOverflowCount,
        _values
            .OrderBy(pair => pair.Key)
            .Select(pair => new AccessTraceBinValueCount(pair.Key, pair.Value))
            .ToArray());
}
