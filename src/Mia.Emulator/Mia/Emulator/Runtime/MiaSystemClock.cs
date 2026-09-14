// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Mia.Emulator.Runtime;

/// <summary>
/// Machine-owned 13 MHz ASIC reference-clock domain. CPU cores report elapsed
/// work to the machine, while ASIC peripherals schedule their own deadlines here.
/// Advancing a large interval visits deadlines chronologically so periodic
/// hardware catches up without drifting or depending on instruction cadence.
/// </summary>
internal sealed class MiaSystemClock
{
    MiaSystemClockEventEntry? _nextEvent;
    readonly List<MiaSystemClockEventEntry> _eventPool = [];
    long _synchronizedAvrCycles;
    int _avrToAsicRemainder;

    public const int AvrCyclesPerSecond = 12_000_000;
    public const int AsicCyclesPerSecond = 13_000_000;
    const int AvrToAsicNumerator = 13;
    const int AvrToAsicDenominator = 12;

    public long Cycles { get; private set; }

    public long NextEventCycle => _nextEvent?.Cycle ?? long.MaxValue;

    /// <summary>
    /// Returns the greatest number of additional AVR cycles whose 13:12 clock
    /// conversion remains strictly before <paramref name="exclusiveAsicCycle"/>.
    /// This is used only to collapse CPU work proven to have no observable
    /// effect; the instruction immediately crossing a device deadline still
    /// executes normally.
    /// </summary>
    public long GetMaximumAdditionalAvrCyclesBefore(long exclusiveAsicCycle)
    {
        if (exclusiveAsicCycle <= Cycles)
        {
            return 0;
        }

        long asicCycles = exclusiveAsicCycle - Cycles;

        // floor((12 * asicCycles - 1 - phase) / 13), rearranged so the
        // intermediate multiplication cannot overflow Int64.
        long wholeGroups = asicCycles / AvrToAsicNumerator;
        long partialNumerator =
            asicCycles % AvrToAsicNumerator * AvrToAsicDenominator -
            1 -
            _avrToAsicRemainder;
        long partialGroups = partialNumerator >= 0
            ? partialNumerator / AvrToAsicNumerator
            : -1;
        return Math.Max(
            0,
            wholeGroups * AvrToAsicDenominator + partialGroups);
    }

    /// <summary>
    /// Advances the ASIC reference clock by the AVR work completed since the
    /// previous call. The hardware clocks are exactly 13:12, and retaining the
    /// rational remainder prevents phase drift across small instruction steps.
    /// Direct ASIC-only advances (for example, a future CPU sleep) remain valid
    /// and are not overwritten by the next synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SynchronizeAvrCycles(long totalAvrCycles)
    {
        if (totalAvrCycles < _synchronizedAvrCycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalAvrCycles),
                "The AVR clock cannot run backwards.");
        }

        var elapsedAvrCycles = totalAvrCycles - _synchronizedAvrCycles;
        if (elapsedAvrCycles == 0)
        {
            return;
        }

        (long elapsedAsicCycles, int nextRemainder) =
            ConvertAvrCycles(elapsedAvrCycles, totalAvrCycles);
        if (elapsedAsicCycles > long.MaxValue - Cycles)
        {
            throw new ArgumentOutOfRangeException(nameof(totalAvrCycles));
        }

        AdvanceBy(elapsedAsicCycles);
        _synchronizedAvrCycles = totalAvrCycles;
        _avrToAsicRemainder = nextRemainder;
    }

    (long Cycles, int Remainder) ConvertAvrCycles(
        long elapsedAvrCycles,
        long totalAvrCycles)
    {
        if (elapsedAvrCycles <=
            (long.MaxValue - (AvrToAsicDenominator - 1)) / AvrToAsicNumerator)
        {
            long scaled = elapsedAvrCycles * AvrToAsicNumerator +
                _avrToAsicRemainder;
            return (
                scaled / AvrToAsicDenominator,
                (int)(scaled % AvrToAsicDenominator));
        }

        long wholeGroups = elapsedAvrCycles / AvrToAsicDenominator;
        long partialNumerator =
            (elapsedAvrCycles % AvrToAsicDenominator) *
                AvrToAsicNumerator +
            _avrToAsicRemainder;
        if (wholeGroups > long.MaxValue / AvrToAsicNumerator)
        {
            throw new ArgumentOutOfRangeException(nameof(totalAvrCycles));
        }

        long cycles = wholeGroups * AvrToAsicNumerator;
        long partialCycles = partialNumerator / AvrToAsicDenominator;
        if (cycles > long.MaxValue - partialCycles)
        {
            throw new ArgumentOutOfRangeException(nameof(totalAvrCycles));
        }
        return (
            cycles + partialCycles,
            (int)(partialNumerator % AvrToAsicDenominator));
    }

    public Action Schedule(Action callback, long delayCycles)
        => ScheduleCore(worker: null, callback, delayCycles);

    public Action Schedule(
        MiaWorker worker,
        Action callback,
        long delayCycles)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return ScheduleCore(worker, callback, delayCycles);
    }

    public Action ScheduleAt(Action callback, long cycle) =>
        ScheduleAtCore(worker: null, callback, cycle);

    public Action ScheduleAt(
        MiaWorker worker,
        Action callback,
        long cycle)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return ScheduleAtCore(worker, callback, cycle);
    }

    Action ScheduleCore(
        MiaWorker? worker,
        Action callback,
        long delayCycles)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delayCycles);
        if (Cycles > long.MaxValue - delayCycles)
        {
            throw new ArgumentOutOfRangeException(nameof(delayCycles));
        }

        return ScheduleAtCore(worker, callback, Cycles + delayCycles);
    }

    Action ScheduleAtCore(MiaWorker? worker, Action callback, long dueCycle)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (dueCycle <= Cycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueCycle),
                "A clock event must be scheduled in the future.");
        }
        MiaSystemClockEventEntry entry = RentEvent();
        entry.Cycle = dueCycle;
        entry.Callback = callback;
        entry.Worker = worker;
        InsertEvent(entry);
        return callback;
    }

    MiaSystemClockEventEntry RentEvent()
    {
        if (_eventPool.Count != 0)
        {
            MiaSystemClockEventEntry entry = _eventPool[^1];
            _eventPool.RemoveAt(_eventPool.Count - 1);
            return entry;
        }
        return new MiaSystemClockEventEntry();
    }

    void InsertEvent(MiaSystemClockEventEntry entry)
    {
        MiaSystemClockEventEntry? current = _nextEvent;
        MiaSystemClockEventEntry? previous = null;
        while (current is not null && current.Cycle <= entry.Cycle)
        {
            previous = current;
            current = current.Next;
        }

        entry.Next = current;
        if (previous is null)
        {
            _nextEvent = entry;
        }
        else
        {
            previous.Next = entry;
        }
    }

    public bool Reschedule(Action callback, long delayCycles)
    {
        if (!Cancel(callback))
        {
            return false;
        }
        Schedule(callback, delayCycles);
        return true;
    }

    public bool Cancel(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        MiaSystemClockEventEntry? current = _nextEvent;
        MiaSystemClockEventEntry? previous = null;
        while (current is not null)
        {
            if (ReferenceEquals(current.Callback, callback))
            {
                return RemoveEvent(current, previous);
            }
            previous = current;
            current = current.Next;
        }
        return false;
    }

    bool RemoveEvent(
        MiaSystemClockEventEntry current,
        MiaSystemClockEventEntry? previous)
    {
        if (previous is null)
        {
            _nextEvent = current.Next;
        }
        else
        {
            previous.Next = current.Next;
        }
        Recycle(current);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceTo(long cycle)
    {
        if (cycle < Cycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cycle),
                "The system clock cannot run backwards.");
        }
        if (_nextEvent is null || _nextEvent.Cycle > cycle)
        {
            Cycles = cycle;
            return;
        }
        AdvanceToWithEvents(cycle);
    }

    public void AdvanceBy(long cycles)
    {
        if (cycles < 0 || Cycles > long.MaxValue - cycles)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }
        AdvanceTo(Cycles + cycles);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void AdvanceToWithEvents(long targetCycle)
    {
        while (_nextEvent is { } entry && entry.Cycle <= targetCycle)
        {
            _nextEvent = entry.Next;
            Cycles = entry.Cycle;
            var callback = entry.Callback;
            var worker = entry.Worker;
            Recycle(entry);
            if (worker is null)
            {
                callback();
            }
            else
            {
                worker.Invoke(callback);
            }
        }
        Cycles = targetCycle;
    }

    void Recycle(MiaSystemClockEventEntry entry)
    {
        entry.Callback = null!;
        entry.Worker = null;
        entry.Next = null;
        if (_eventPool.Count < 16)
        {
            _eventPool.Add(entry);
        }
    }
}
