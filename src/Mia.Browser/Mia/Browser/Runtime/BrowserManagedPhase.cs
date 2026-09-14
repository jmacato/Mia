// SPDX-License-Identifier: MIT

namespace Mia.Browser.Runtime;

internal enum BrowserManagedPhase
{
    Stopped,
    LoadingPersistence,
    CreatingMachine,
    ConfiguringMachine,
    StartingRunLoop,
    RunningBatch,
    Pacing,
    RenderingAudio,
    Stopping,
    FlushingPersistence,
    DisposingMachine,
    Faulted,
}
