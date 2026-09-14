// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicInterruptRoute(
    byte PhysicalSource,
    byte ProcessDestination,
    byte CheckByte,
    byte Marker,
    byte Reserved);
