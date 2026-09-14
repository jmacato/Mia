// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicTimeGeneratorActionExecution(
    byte ProgramSelector,
    int SchedulePortAddress,
    byte ActionId,
    ushort Operand,
    ushort QuarterBit,
    int OccurrenceIndex,
    int OccurrenceCount);
