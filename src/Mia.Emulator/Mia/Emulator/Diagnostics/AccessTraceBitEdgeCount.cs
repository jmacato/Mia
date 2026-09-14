// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceBitEdgeCount(
    int Bit,
    long Rises,
    long Falls);
