// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

internal readonly record struct MiaDataWriteObservation(
    long Cycle,
    int Pc,
    int Address,
    byte OldValue,
    byte NewValue,
    byte Mask);
