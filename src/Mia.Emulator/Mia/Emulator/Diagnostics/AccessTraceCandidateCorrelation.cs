// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceCandidateCorrelation(
    string CandidateId,
    IReadOnlyList<AccessTraceRunCorrelation> Runs,
    IReadOnlyList<AccessTraceRobustnessResult> Robustness,
    IReadOnlyList<double> PlaceboFrequenciesHz,
    IReadOnlyList<double> PlaceboPowers,
    double MedianServiceControlEffect,
    double MedianServiceControlRatio,
    bool AllPairEffectsPositive,
    double MedianServiceR2,
    double PhaseResultant,
    double OffFrequencyPValue,
    double RotationPValue,
    double NullStandardizedEffect,
    double AdjustedQValue,
    int RotationNullCount,
    bool Accepted,
    bool HasExternalPhaseAnchor,
    string? AnchoredStateNames);
