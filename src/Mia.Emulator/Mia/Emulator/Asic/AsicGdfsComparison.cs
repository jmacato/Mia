// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicGdfsComparison(
    int Low,
    int High,
    int KeyLow,
    int KeyHigh);
