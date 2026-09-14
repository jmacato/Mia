// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicRomSchedulerContextMetadata(
    int DescriptorAddress,
    byte Process,
    int TaskEntry,
    int HardwareStackFloor,
    int HardwareStackTop);
