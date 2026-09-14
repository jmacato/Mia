// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class RealTimePacerTests
{
    [Fact]
    public void LateDesktopPacerRetainsDebtAndCatchesOriginalTimeline()
    {
        var clock = new RealTimePacerTestsFakeHostClock();
        var pacer = new RealTimePacer(cyclesPerSecond: 1_000, hostClock: clock);

        clock.Timestamp = 500;
        pacer.Pace(cycles: 1, CancellationToken.None);

        Assert.Equal(499, pacer.DriftMilliseconds);
        Assert.Equal(0, clock.WaitCount);

        pacer.Pace(cycles: 501, CancellationToken.None);

        Assert.Equal(501, clock.Timestamp);
        Assert.Equal(1, clock.WaitCount);
        Assert.Equal(0, pacer.DriftMilliseconds);
    }

    [Fact]
    public async Task LateAsyncPacerYieldsWithoutAddingAnotherDelay()
    {
        var clock = new RealTimePacerTestsFakeHostClock();
        var pacer = new RealTimePacer(cyclesPerSecond: 1_000, hostClock: clock);

        clock.Timestamp = 500;
        await pacer.PaceAsync(cycles: 1, CancellationToken.None);

        Assert.Equal(499, pacer.DriftMilliseconds);
        Assert.Equal(0, clock.DelayCount);
        Assert.Equal(1, clock.YieldCount);
    }

    [Fact]
    public async Task EarlyAsyncPacerUsesATimedWaitInsteadOfBusyYielding()
    {
        var clock = new RealTimePacerTestsFakeHostClock();
        var pacer = new RealTimePacer(
            cyclesPerSecond: 1_000,
            hostClock: clock);

        await pacer.PaceAsync(cycles: 1, CancellationToken.None);

        Assert.Equal(1, clock.Timestamp);
        Assert.Equal(1, clock.DelayCount);
        Assert.Equal(0, clock.YieldCount);
        Assert.Equal(0, pacer.DriftMilliseconds);
    }

    [Fact]
    public async Task AsyncPacerReanchorsExcessiveConfiguredCatchUpDebt()
    {
        var clock = new RealTimePacerTestsFakeHostClock();
        var pacer = new RealTimePacer(
            cyclesPerSecond: 1_000,
            hostClock: clock,
            maximumCatchUpDebt: TimeSpan.FromMilliseconds(250));

        clock.Timestamp = 500;
        await pacer.PaceAsync(cycles: 1, CancellationToken.None);

        Assert.Equal(0, pacer.DriftMilliseconds);
        Assert.Equal(0, clock.DelayCount);
        Assert.Equal(1, clock.YieldCount);

        clock.Timestamp = 501;
        await pacer.PaceAsync(cycles: 2, CancellationToken.None);

        Assert.Equal(0, pacer.DriftMilliseconds);
    }
}
