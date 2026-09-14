// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceTransitionCount(
    ulong OldValue,
    ulong NewValue,
    long Count);
