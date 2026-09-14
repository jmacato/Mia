// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

internal interface IRealTimePacerClock
{
    long Frequency { get; }

    long GetTimestamp();

    void Wait(int milliseconds, CancellationToken cancellationToken);

    ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken);

    ValueTask YieldAsync();
}
