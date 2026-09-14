// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

public sealed class ArmModemRomCallException : Exception
{
    public ArmModemRomCallException() { }

    public ArmModemRomCallException(string message) : base(message) { }

    public ArmModemRomCallException(string message, Exception innerException)
        : base(message, innerException) { }

    public ArmModemRomCallException(uint address)
    {
        Address = address;
    }

    public uint Address { get; }
}
