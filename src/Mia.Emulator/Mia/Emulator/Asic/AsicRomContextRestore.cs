// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRomContextRestore(
    AsicRomSchedulerState State,
    int ContextAddress,
    AsicRomCpuContext Context,
    AsicRomSchedulerContextMetadata Metadata);
