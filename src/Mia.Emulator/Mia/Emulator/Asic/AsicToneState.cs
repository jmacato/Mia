// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

/// <summary>
/// Immutable firmware-visible tone-generator register state at an ASIC
/// reference-clock cycle. The semantics of the fourth register have not yet
/// been recovered, so its value is retained without interpretation.
/// </summary>
internal readonly record struct AsicToneState(
    long Cycle,
    byte Control,
    byte Width,
    byte Reload,
    byte Unknown,
    byte DtmfCode = 0);
