// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRomMemoryTransfer(
    int Source,
    int Destination,
    int Count,
    int PhysicalSource = 0);
