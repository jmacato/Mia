// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly ref struct GsmSmsSubmit(
    string destination,
    bool international,
    byte alphabet,
    int userDataLength,
    ReadOnlySpan<byte> userData)
{
    public string Destination { get; } = destination;
    public bool International { get; } = international;
    public byte Alphabet { get; } = alphabet;
    public int UserDataLength { get; } = userDataLength;
    public ReadOnlySpan<byte> UserData { get; } = userData;

    public static bool TryRead(ReadOnlySpan<byte> tpdu, out GsmSmsSubmit submit)
    {
        submit = default;
        if (!TryReadAddress(tpdu, out var address) ||
            !TryReadUserData(tpdu, address, out submit))
        {
            return false;
        }
        return true;
    }

    static bool TryReadAddress(
        ReadOnlySpan<byte> tpdu,
        out GsmSmsSubmitAddress address)
    {
        address = default;
        if (tpdu.Length < 7 || (tpdu[0] & 0x03) != 0x01)
        {
            return false;
        }

        var digitCount = tpdu[2];
        var encodedLength = (digitCount + 1) / 2;
        const int encodedOffset = 4;
        if (digitCount is < 1 or > 20 ||
            encodedOffset + encodedLength + 3 > tpdu.Length ||
            !GsmSmsCodec.TryDecodeSemiOctets(
                tpdu.Slice(encodedOffset, encodedLength),
                digitCount,
                out var destination))
        {
            return false;
        }

        address = new(
            tpdu[0],
            destination,
            (tpdu[3] & 0x70) == 0x10,
            encodedOffset + encodedLength);
        return true;
    }

    static bool TryReadUserData(
        ReadOnlySpan<byte> tpdu,
        GsmSmsSubmitAddress address,
        out GsmSmsSubmit submit)
    {
        submit = default;
        var offset = address.NextOffset + 1;
        var dataCodingScheme = tpdu[offset++];
        var validityPeriodLength = ((address.FirstOctet >> 3) & 0x03) switch
        {
            0 => 0,
            2 => 1,
            _ => 7,
        };
        if (offset + validityPeriodLength >= tpdu.Length)
        {
            return false;
        }

        offset += validityPeriodLength;
        submit = new(
            address.Destination,
            address.International,
            (byte)(dataCodingScheme & 0x0c),
            tpdu[offset++],
            tpdu[offset..]);
        return true;
    }
}
