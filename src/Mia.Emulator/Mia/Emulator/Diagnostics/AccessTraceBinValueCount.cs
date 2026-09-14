// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceBinValueCount(
    ulong Value,
    long Count);
