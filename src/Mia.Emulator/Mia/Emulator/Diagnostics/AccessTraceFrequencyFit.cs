// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceFrequencyFit(
    double FrequencyHz,
    double Intercept,
    double Drift,
    double SineCoefficient,
    double CosineCoefficient,
    double Amplitude,
    double PhaseRadians,
    double Power,
    double R2,
    int IncludedBinCount);
