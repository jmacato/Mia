// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerSynchronousWaiter : IDisposable
{
    public readonly AutoResetEvent Completed = new(false);

    public void Dispose() => Completed.Dispose();
}
#endif
