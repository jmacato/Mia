// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicRomSchedulerState
{
    internal Dictionary<int, AsicRomCpuContext> Contexts { get; } = [];
    internal Dictionary<int, AsicRomSchedulerContextMetadata> Metadata { get; } = [];
    internal Dictionary<int, AsicRomInterruptCapture> InterruptCaptures { get; } = [];
    internal long CapturedInterruptRestoreCount;
    internal long RejectedUncapturedInterruptRestoreCount;
}
