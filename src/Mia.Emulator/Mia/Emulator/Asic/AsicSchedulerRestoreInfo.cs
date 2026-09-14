// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicSchedulerRestoreInfo(
    long CapturedInterruptRestores,
    long RejectedUncapturedInterruptRestores);
