// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicTimeGeneratorActionSchedule(
    int Slot,
    long Generation,
    long FrameStartCycle,
    byte ProgramSelector,
    int Address,
    byte ActionId,
    ushort Operand,
    AsicTimeGeneratorActionDefinition Definition);
