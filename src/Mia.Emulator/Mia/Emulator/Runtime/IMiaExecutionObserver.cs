// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Runtime;

/// <summary>
/// Optional instruction-boundary observer used only by the reverse-engineering
/// CLI. Normal emulation leaves this unset, so probes cannot become part of the
/// firmware-visible hardware model.
/// </summary>
internal interface IMiaExecutionObserver
{
    /// <returns>A stop reason, or <see langword="null"/> to continue.</returns>
    string? BeforeWorkItem(MiaMachine machine);

    void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result);
}
