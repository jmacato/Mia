// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class RealTimePacerTestsFakeHostClock : IRealTimePacerClock
{
    public long Frequency => 1_000;

    public long Timestamp { get; set; }

    public int WaitCount { get; private set; }

    public int DelayCount { get; private set; }

    public int YieldCount { get; private set; }

    public long GetTimestamp() => Timestamp;

    public void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitCount++;
        Timestamp += milliseconds;
    }

    public ValueTask DelayAsync(
        int milliseconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DelayCount++;
        Timestamp += milliseconds;
        return ValueTask.CompletedTask;
    }

    public ValueTask YieldAsync()
    {
        YieldCount++;
        return ValueTask.CompletedTask;
    }
}
