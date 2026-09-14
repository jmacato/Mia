// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceCorrelationCandidateInput(
    string CandidateId,
    IReadOnlyList<AccessTraceCorrelationRunInput> Runs);
