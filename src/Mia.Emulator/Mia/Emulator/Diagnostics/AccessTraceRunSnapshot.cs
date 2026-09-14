// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceRunSnapshot(
    string PairId,
    bool IsService,
    AccessTraceSnapshot Snapshot);
