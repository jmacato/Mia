// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRomSoftwareStackWindow(
    int LogicalFirst,
    int LogicalLast,
    int PhysicalFirst);
