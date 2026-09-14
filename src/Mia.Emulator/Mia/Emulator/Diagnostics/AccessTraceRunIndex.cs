// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceRunIndex(
    string PairId,
    bool IsService,
    AccessTraceSnapshot Snapshot,
    Dictionary<string, (
        AccessTraceBus Bus,
        AccessTraceOperation Operation,
        AccessTraceOperationSnapshot Snapshot)> Operations,
    Dictionary<(AccessTraceBus, AccessTraceOperation), long[]> Totals);
