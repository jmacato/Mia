// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicProcessDescriptorInfo(
    byte Process,
    int Address,
    int TaskEntry,
    byte State);
