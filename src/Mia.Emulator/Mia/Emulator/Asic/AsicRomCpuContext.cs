// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicRomCpuContext(
    byte[] Registers,
    int Pc,
    int StackPointer,
    byte RampD,
    byte RampX,
    byte RampY,
    byte RampZ,
    byte Eind,
    byte Sreg,
    AsicRomHardwareStackSnapshot? HardwareStack,
    int AddressEnvironmentDescriptorAddress);
