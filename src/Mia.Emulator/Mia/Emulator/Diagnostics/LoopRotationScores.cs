// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record LoopRotationScores(
    string PairId,
    bool IsService,
    long[][][][] FrequencyPhaseCellCounts);
