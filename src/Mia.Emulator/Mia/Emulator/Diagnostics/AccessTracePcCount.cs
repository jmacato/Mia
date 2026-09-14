// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTracePcCount(
    ulong InstructionAddress,
    long Count);
