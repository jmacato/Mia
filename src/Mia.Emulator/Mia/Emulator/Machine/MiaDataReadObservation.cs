// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

internal readonly record struct MiaDataReadObservation(
    long Cycle,
    int Pc,
    int Address,
    byte Value);
