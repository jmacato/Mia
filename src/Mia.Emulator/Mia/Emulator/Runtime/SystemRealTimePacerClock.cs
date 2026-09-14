// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Mia.Emulator.Runtime;

internal sealed class SystemRealTimePacerClock : IRealTimePacerClock
{
    public static SystemRealTimePacerClock Instance { get; } = new();

    public long Frequency => Stopwatch.Frequency;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            Task.Delay(milliseconds, cancellationToken).Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DelayAsync(
        int milliseconds,
        CancellationToken cancellationToken) =>
        new(Task.Delay(milliseconds, cancellationToken));

    public async ValueTask YieldAsync()
    {
        await Task.Yield();
    }
}
