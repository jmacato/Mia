// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal sealed class ArmModemAddressRegion(
    uint baseAddress,
    uint length,
    byte[] backing)
{
    public ArmModemMemoryRegion? Resolve(uint address, int size) =>
        address >= baseAddress && address - baseAddress + size <= length
            ? new(backing, checked((int)(address - baseAddress)))
            : null;
}
