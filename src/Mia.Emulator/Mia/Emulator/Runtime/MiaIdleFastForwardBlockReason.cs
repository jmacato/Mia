// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

/// <summary>
/// Why a visit to the verified firmware idle-loop entry could not be safely
/// accelerated. These reasons describe only a candidate at the resolved hook.
/// </summary>
internal enum MiaIdleFastForwardBlockReason
{
    None,
    Disabled,
    ExecutionObserverAttached,
    InterruptPending,
    DeferredTickPending,
    InterruptControllerActive,
    FirmwareSignatureChanged,
    ModemStopped,
    ModemNotQuiescent,
    DataHookInstalled,
    SchedulerActive,
    RegisterStateChanged,
    NoWholeLoopBeforeDeadline,
}
