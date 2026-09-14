// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

public sealed class ArmModemBusAccessException : Exception
{
    public ArmModemBusAccessException() { }

    ArmModemBusAccessException(string message)
        : base(message)
    {
    }

    public ArmModemBusAccessException(string message, Exception innerException)
        : base(message, innerException) { }

    public static ArmModemBusAccessException Read(uint pc, uint address, int size) =>
        new($"unmapped ARM read{size} pc=0x{pc:x8} address=0x{address:x8}");

    public static ArmModemBusAccessException Write(
        uint pc,
        uint address,
        uint value,
        int size) =>
        new($"unmapped ARM write{size} pc=0x{pc:x8} address=0x{address:x8} " +
            $"value=0x{value:x8}");
}
