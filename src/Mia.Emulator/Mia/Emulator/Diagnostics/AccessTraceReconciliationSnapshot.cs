// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceReconciliationSnapshot(
    AccessTraceBus Bus,
    AccessTraceOperation Operation,
    long CallbackCount,
    long AggregatedCount,
    long OutsideWindowCount);
