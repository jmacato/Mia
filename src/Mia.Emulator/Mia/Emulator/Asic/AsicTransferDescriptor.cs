// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicTransferDescriptor(
    ushort Address,
    ushort Length,
    byte Control);
