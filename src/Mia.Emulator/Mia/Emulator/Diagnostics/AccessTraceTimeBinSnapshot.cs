// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceTimeBinSnapshot(
    int Index,
    long EventCount,
    long ChangeCount,
    ulong LastValue,
    long ValueOverflowCount,
    IReadOnlyList<AccessTraceBinValueCount> Values);
