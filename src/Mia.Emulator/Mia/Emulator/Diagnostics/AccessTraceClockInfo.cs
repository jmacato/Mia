// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceClockInfo(
    string MachineCycleDomain,
    int MachineCyclesPerSecond,
    int AvrCyclesPerSecond,
    int AvrToMachineNumerator,
    int AvrToMachineDenominator,
    long AvrToMachineOffset);
