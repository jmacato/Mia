// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemRamWrite(
    long Cycle,
    uint ProgramCounter,
    uint Address,
    int Size,
    uint Value);
