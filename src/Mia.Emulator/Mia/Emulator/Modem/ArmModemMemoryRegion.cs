// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemMemoryRegion(
    byte[] Backing,
    int Offset);
