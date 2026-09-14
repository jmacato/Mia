// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemFlashCommand(
    uint Address,
    uint Value,
    int Size)
{
    public uint BankBase => Address & 0xffff0000;

    public bool IsParameterBank => BankBase is
        0x011c0000 or 0x011d0000 or 0x011e0000 or 0x011f0000;
}
