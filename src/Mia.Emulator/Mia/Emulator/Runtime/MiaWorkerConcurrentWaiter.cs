// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerConcurrentWaiter : IDisposable
{
    public readonly AutoResetEvent Completed = new(false);
    public bool InUse;

    public void Dispose() => Completed.Dispose();
}
#endif
