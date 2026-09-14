// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Infrared;

internal readonly record struct ArmModemInfraredObexHeaders(
    bool Include,
    byte[] Name,
    byte[] Type,
    byte[] Length)
{
    public int PacketOverhead =>
        3 + Name.Length + Type.Length + Length.Length;

    public void CopyTo(Span<byte> destination)
    {
        Name.CopyTo(destination);
        Type.CopyTo(destination[Name.Length..]);
        Length.CopyTo(destination[(Name.Length + Type.Length)..]);
    }
}
