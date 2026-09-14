// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemMmioAccess(
    long Cycle,
    uint Pc,
    uint Address,
    int Size,
    bool IsWrite,
    uint Value);
