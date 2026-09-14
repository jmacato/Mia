// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal interface IMiaWorkerWorkItem
{
    void Execute();
}
#endif
