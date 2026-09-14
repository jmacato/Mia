// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceObservation(
    AccessTraceAddressKey Key,
    AccessTraceOperation Operation,
    long Cycle,
    ulong Value,
    ulong InstructionAddress,
    ulong? OldValue,
    ulong? Mask,
    int BitWidth,
    bool ConsumedWrite = false);
