// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicRomInterruptCapture(
    AsicRomInterruptCapturePhase Phase,
    AsicRomCpuContext Context);
