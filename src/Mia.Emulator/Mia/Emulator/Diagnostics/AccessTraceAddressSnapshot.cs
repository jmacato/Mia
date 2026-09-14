// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceAddressSnapshot(
    AccessTraceAddressKey Key,
    long TotalCount,
    long ReadCount,
    long WriteCount,
    long ChangeCount,
    long FirstCycle,
    long LastCycle,
    IReadOnlyList<AccessTraceOperationSnapshot> Operations);
