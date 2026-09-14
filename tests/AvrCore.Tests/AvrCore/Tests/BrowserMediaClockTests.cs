// SPDX-License-Identifier: MIT

using Mia.Browser.Runtime;
using Xunit;

namespace AvrCore.Tests;

public sealed class BrowserMediaClockTests
{
    [Fact]
    public void FramesFollowConsumedAudioRatherThanProducerOrUiTime()
    {
        var shown = new List<byte>();
        var clock = new BrowserMediaClock(0, frame => shown.Add(frame.Span[0]));
        clock.Enqueue(13_000_000, new byte[] { 1 });
        clock.Enqueue(26_000_000, new byte[] { 2 });
        clock.Advance(47_999);
        Assert.Empty(shown);
        clock.Advance(48_000);
        Assert.Equal(new byte[] { 1 }, shown);
        clock.Advance(48_000);
        Assert.Single(shown);
        clock.Advance(96_000);
        Assert.Equal(new byte[] { 1, 2 }, shown);
    }

    [Fact]
    public void DelayedPresentationStaysBoundedAndSurvivesCounterWrap()
    {
        var shown = new List<byte>();
        var clock = new BrowserMediaClock(uint.MaxValue - 47_999, frame => shown.Add(frame.Span[0]));
        for (byte i = 1; i <= 100; i++) clock.Enqueue(i * 130_000L, new byte[] { i });
        Assert.Equal(16, clock.PendingFrames);
        clock.Advance(0);
        Assert.Equal(new byte[] { 100 }, shown);
        Assert.Equal(0, clock.PendingFrames);
    }

    [Fact]
    public void CoreWaitsOnlyWhenTheAudioReserveHasBeenReplenished()
    {
        var clock = new BrowserMediaClock(0, _ => { });
        Assert.Equal(0, clock.GetDelayMilliseconds(0));
        Assert.Equal(0, clock.GetDelayMilliseconds(13_000_000 * 60 / 1_000));
        Assert.Equal(0, clock.GetDelayMilliseconds(13_000_000 / 10));
        Assert.Equal(6, clock.GetDelayMilliseconds(13_000_000 / 5));
        clock.Advance(4_800);
        Assert.Equal(0, clock.GetDelayMilliseconds(13_000_000 / 5));
    }
}
