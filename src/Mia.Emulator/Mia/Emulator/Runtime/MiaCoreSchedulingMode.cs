// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

internal enum MiaCoreSchedulingMode
{
    /// <summary>Runs the modem up to the AVR clock before each AVR instruction.</summary>
    CycleLocked,

    /// <summary>
    /// Runs AVR and modem instruction streams concurrently and exchanges
    /// modem-to-ASIC bytes at work-batch boundaries. This preserves all CPU,
    /// MMIO, FIFO, and IRQ execution while relaxing cross-core event ordering.
    /// </summary>
    CoarseParallel,
}
