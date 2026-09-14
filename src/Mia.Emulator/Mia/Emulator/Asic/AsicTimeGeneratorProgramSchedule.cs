// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicTimeGeneratorProgramSchedule(
    int Slot,
    long Generation,
    long FrameStartCycle,
    byte ProgramSelector,
    int Address,
    ReadOnlyMemory<byte> Bytes,
    byte ScheduledActionToken);
