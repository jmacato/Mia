// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal sealed class ArmModemBusScheduledPeripheralEvent(
    Action<long> callback,
    Action cancelled,
    long cycle,
    long sequence) : IDisposable
{
    public Action<long> Callback { get; } = callback;

    // Mirrors this event's own priority-queue key so callers can read it
    // straight off a Peek() result instead of a TryPeek(out ...) pair, which
    // the disposable-ownership analyzer misreads as a new allocation.
    public long Cycle { get; } = cycle;

    public long Sequence { get; } = sequence;

    public bool IsCancelled { get; private set; }

    public void Dispose()
    {
        if (IsCancelled)
        {
            return;
        }
        IsCancelled = true;
        cancelled();
    }
}
