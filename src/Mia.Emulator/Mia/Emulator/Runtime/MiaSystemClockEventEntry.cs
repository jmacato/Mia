// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

internal sealed class MiaSystemClockEventEntry
{
    public long Cycle;
    public Action Callback = null!;
    public MiaWorker? Worker;
    public MiaSystemClockEventEntry? Next;
}
