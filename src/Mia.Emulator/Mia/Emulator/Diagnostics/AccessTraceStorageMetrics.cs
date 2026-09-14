// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceStorageMetrics(
    int AddressCount,
    int TimeBinCellCount,
    int BinValueCellCount,
    int ValueHistogramCellCount,
    int TransitionCellCount,
    int ProgramCounterCellCount,
    long BinValueOverflowCount,
    long ValueHistogramOverflowCount,
    long TransitionOverflowCount,
    long ProgramCounterOverflowCount);
