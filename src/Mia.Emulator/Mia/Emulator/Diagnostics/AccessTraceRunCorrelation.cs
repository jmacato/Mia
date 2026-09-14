// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceRunCorrelation(
    string PairId,
    bool IsService,
    AccessTraceTargetPowers AccessRatePowers,
    AccessTraceTargetPowers TrafficSharePowers,
    double CategoricalAmplitude,
    double CategoricalR2,
    double CategoricalLoopFrequencyHz,
    double ConditionalLoopPower,
    double ConditionalColorHarmonicPower,
    double ConditionalLcdFramePower,
    double ConditionalLcdEdgePower,
    IReadOnlyList<AccessTraceStateValueCount> StateValueCounts,
    double CyclicTemplateCorrelation,
    int CyclicOffset,
    double CyclicPhaseRadians);
