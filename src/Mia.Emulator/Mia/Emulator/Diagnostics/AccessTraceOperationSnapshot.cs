// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceOperationSnapshot(
    AccessTraceOperation Operation,
    long Count,
    long ChangeCount,
    long ConsumedWriteCount,
    long FirstCycle,
    long LastCycle,
    ulong? InitialHeldValue,
    int BitWidth,
    long ValueOverflowCount,
    long TransitionOverflowCount,
    long ProgramCounterOverflowCount,
    long MaskOverflowCount,
    long BinValueOverflowCount,
    IReadOnlyList<AccessTraceCount> Values,
    IReadOnlyList<AccessTraceTransitionCount> Transitions,
    IReadOnlyList<AccessTraceBitEdgeCount> BitEdges,
    IReadOnlyList<AccessTracePcCount> ProgramCounters,
    IReadOnlyList<AccessTraceCount> Masks,
    IReadOnlyList<AccessTraceTimeBinSnapshot> TimeBins);
