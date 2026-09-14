// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal enum AsicI2cTransferPhase
{
    NotStarted,
    Address,
    Inactive,
    Read,
    Write,
}
