// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal sealed record ArmModemFlashProfile(
    string Name,
    uint Size,
    ushort ManufacturerId,
    ushort DeviceId)
{
    public static ArmModemFlashProfile StM36Dr216C { get; } =
        new("STM M36DR216C", 0x00200000, 0x0020, 0x0093);
}
