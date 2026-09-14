// SPDX-License-Identifier: MIT

namespace AvrCore.Execution;

/// <summary>
/// Observes one completed access through <see cref="Cpu.ReadData(int)"/>.
/// </summary>
public delegate void CpuDataReadObserver(int address, byte value);
