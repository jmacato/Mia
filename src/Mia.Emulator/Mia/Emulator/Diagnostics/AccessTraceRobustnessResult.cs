// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceRobustnessResult(
    int ExcludedWholeLoops,
    AccessTraceWindowHalf Half,
    bool AllPairEffectsPositive,
    double MedianServiceControlRatio,
    double MedianEffect,
    double RelativeEffectChange,
    double MedianServiceR2,
    double PhaseResultant,
    bool TargetPowersPass,
    bool ValuePhasePass);
