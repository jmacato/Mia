// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicSchedulerContextInfo(
    int Address,
    int DescriptorAddress,
    byte Process,
    int TaskEntry,
    int SavedPc,
    int SavedStackPointer,
    int SoftwareStackPointer,
    byte RampD,
    byte RampX,
    byte RampZ,
    byte Eind,
    byte Sreg,
    ReadOnlyMemory<byte> Registers,
    int HardwareStackStart,
    ReadOnlyMemory<byte> HardwareStack,
    byte State,
    bool Runnable);
