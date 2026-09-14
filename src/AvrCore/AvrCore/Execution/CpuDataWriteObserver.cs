// SPDX-License-Identifier: MIT

namespace AvrCore.Execution;

/// <summary>
/// Observes one completed access through <see cref="Cpu.WriteData(int, byte, byte)"/>.
/// </summary>
public delegate void CpuDataWriteObserver(
    int address,
    byte oldValue,
    byte newValue,
    byte mask,
    bool hookConsumed);
