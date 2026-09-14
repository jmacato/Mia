// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaSystemClockTests
{
    [Fact]
    public void AbsoluteScheduleRunsAtRequestedCycle()
    {
        var clock = new MiaSystemClock();
        long observed = -1;

        clock.ScheduleAt(() => observed = clock.Cycles, 37);
        clock.AdvanceTo(36);
        Assert.Equal(-1, observed);

        clock.AdvanceTo(37);

        Assert.Equal(37, observed);
    }

    [Fact]
    public void AvrSynchronizationUsesTheExactThirteenToTwelveRatio()
    {
        var clock = new MiaSystemClock();

        for (var avrCycle = 1; avrCycle <= 12; avrCycle++)
        {
            clock.SynchronizeAvrCycles(avrCycle);
            Assert.Equal(avrCycle * 13 / 12, clock.Cycles);
        }

        clock.SynchronizeAvrCycles(MiaSystemClock.AvrCyclesPerSecond);
        Assert.Equal(MiaSystemClock.AsicCyclesPerSecond, clock.Cycles);
    }

    [Fact]
    public void AvrSynchronizationPreservesIndependentAsicAdvances()
    {
        var clock = new MiaSystemClock();

        clock.AdvanceBy(7);
        clock.SynchronizeAvrCycles(12);
        clock.AdvanceBy(5);
        clock.SynchronizeAvrCycles(24);

        Assert.Equal(38, clock.Cycles);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => clock.SynchronizeAvrCycles(23));
    }

    [Fact]
    public void ExclusiveDeadlineConversionPreservesEveryThirteenToTwelvePhase()
    {
        for (var phase = 0; phase < 12; phase++)
        {
            var clock = new MiaSystemClock();
            clock.SynchronizeAvrCycles(phase);
            clock.AdvanceBy(7);

            for (var gap = 1; gap <= 100; gap++)
            {
                long expected = 0;
                while (((expected + 1) * 13 + phase) / 12 < gap)
                {
                    expected++;
                }

                Assert.Equal(
                    expected,
                    clock.GetMaximumAdditionalAvrCyclesBefore(clock.Cycles + gap));
            }
        }
    }

    [Fact]
    public void EventRunsAtItsDeadlineAndNotBefore()
    {
        var clock = new MiaSystemClock();
        var observedCycles = new List<long>();
        clock.Schedule(() => observedCycles.Add(clock.Cycles), 10);

        clock.AdvanceTo(9);
        Assert.Empty(observedCycles);
        Assert.Equal(10, clock.NextEventCycle);

        clock.AdvanceTo(10);
        Assert.Equal([10L], observedCycles);
        Assert.Equal(long.MaxValue, clock.NextEventCycle);
    }

    [Fact]
    public void EqualDeadlineEventsRunInSchedulingOrder()
    {
        var clock = new MiaSystemClock();
        var observed = new List<int>();
        clock.Schedule(() => observed.Add(1), 7);
        clock.Schedule(() => observed.Add(2), 7);
        clock.Schedule(() => observed.Add(3), 7);

        clock.AdvanceTo(7);

        Assert.Equal([1, 2, 3], observed);
    }

    [Fact]
    public void DeviceDeadlinesRunOnStableWorkersInScheduleOrder()
    {
        var clock = new MiaSystemClock();
        using var first = new MiaWorker("clock first");
        using var second = new MiaWorker("clock second");
        var observed = new List<(int Value, int Thread)>();

        clock.Schedule(
            first,
            () => observed.Add((1, Environment.CurrentManagedThreadId)),
            10);
        clock.Schedule(
            second,
            () => observed.Add((2, Environment.CurrentManagedThreadId)),
            10);

        clock.AdvanceTo(10);

        Assert.Equal([1, 2], observed.Select(item => item.Value));
        Assert.Equal(first.ThreadId, observed[0].Thread);
        Assert.Equal(second.ThreadId, observed[1].Thread);
        Assert.NotEqual(first.ThreadId, second.ThreadId);
    }

    [Fact]
    public void CancelAndRescheduleUseTheCurrentClockPhase()
    {
        var clock = new MiaSystemClock();
        var observedCycles = new List<long>();
        Action callback = () => observedCycles.Add(clock.Cycles);
        clock.Schedule(callback, 10);

        clock.AdvanceTo(3);
        Assert.True(clock.Reschedule(callback, 12));
        Assert.Equal(15, clock.NextEventCycle);

        clock.AdvanceTo(14);
        Assert.Empty(observedCycles);
        Assert.True(clock.Cancel(callback));
        Assert.False(clock.Cancel(callback));
        Assert.False(clock.Reschedule(callback, 1));

        clock.AdvanceTo(100);
        Assert.Empty(observedCycles);
    }

    [Fact]
    public void BulkAdvanceCatchesUpPeriodicEventsWithoutPhaseDrift()
    {
        var clock = new MiaSystemClock();
        var observedCycles = new List<long>();
        Action? tick = null;
        tick = () =>
        {
            observedCycles.Add(clock.Cycles);
            clock.Schedule(tick!, 10);
        };
        clock.Schedule(tick, 10);

        clock.AdvanceBy(35);

        Assert.Equal([10L, 20L, 30L], observedCycles);
        Assert.Equal(35, clock.Cycles);
        Assert.Equal(40, clock.NextEventCycle);
    }

    [Fact]
    public void RejectsInvalidOrOverflowingAdvancesAndSchedules()
    {
        var clock = new MiaSystemClock();

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Schedule(() => { }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceBy(-1));

        clock.AdvanceTo(long.MaxValue);

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(long.MaxValue - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceBy(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Schedule(() => { }, 1));
    }
}
