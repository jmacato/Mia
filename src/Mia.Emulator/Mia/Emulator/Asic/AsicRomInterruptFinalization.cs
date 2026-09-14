// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRomInterruptFinalization(
    AsicRomSchedulerState State,
    int ContextAddress,
    AsicRomInterruptCapture Capture);
