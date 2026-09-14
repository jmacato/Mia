// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

/// <summary>
/// Mutable counters owned exclusively by the AVR worker while hotspot
/// profiling is active.
/// </summary>
internal struct ExecutionHotspotCounts
{
    public long Executions;
    public long BackwardBranches;
}
