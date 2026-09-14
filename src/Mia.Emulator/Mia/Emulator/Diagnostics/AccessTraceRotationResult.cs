// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceRotationResult(
    double ObservedEffect,
    double NullMean,
    double NullStandardDeviation,
    double StandardizedEffect,
    double PValue);
