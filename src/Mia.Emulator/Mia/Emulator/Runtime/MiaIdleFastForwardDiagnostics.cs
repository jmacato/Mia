// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

/// <summary>
/// Snapshot of the opt-in, non-polling idle-fast-forward diagnostics.
/// </summary>
internal readonly record struct MiaIdleFastForwardDiagnostics(
    int? LoopStartWord,
    long CandidateCount,
    long SuccessCount,
    long FastForwardedInstructions,
    MiaIdleFastForwardBlockReason LastBlockReason,
    IReadOnlyList<MiaIdleFastForwardBlockCount> BlockCounts);
