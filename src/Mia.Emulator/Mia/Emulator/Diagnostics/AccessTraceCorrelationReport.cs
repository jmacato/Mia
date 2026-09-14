// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceCorrelationReport(
    IReadOnlyList<string> InventoriedCandidateIds,
    IReadOnlyList<AccessTraceCandidateCorrelation> Candidates,
    int PlaceboFundamentalCount,
    int RotationNullCount,
    double GlobalFalseDiscoveryRate);
