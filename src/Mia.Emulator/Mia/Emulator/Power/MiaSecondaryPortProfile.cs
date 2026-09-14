// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Power;

/// <summary>
/// Physical profile for the incompletely identified I2C device at 0x92/0x93.
/// Identity values are restricted to the exact set accepted by the R8A015
/// firmware check at AVR word address 0x0768a3.
/// </summary>
internal readonly record struct MiaSecondaryPortProfile(
    byte Identity,
    byte Status)
{
    public static MiaSecondaryPortProfile Default => new(0x41, 0x00);
}
