// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRomContextSave(
    AsicRomSchedulerState State,
    int ContextAddress,
    AsicRomSchedulerContextMetadata Metadata,
    AsicRomCpuContext Context);
