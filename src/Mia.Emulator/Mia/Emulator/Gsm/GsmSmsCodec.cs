// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal static class GsmSmsCodec
{
    const int MaximumIncomingSmsTextSeptets = 120;
    // The native T68 SMS transfer path dynamically accepts at most two
    // 20-byte LAPDm information segments. A third segment is acknowledged at
    // CP but never reaches SMS-TL, so keep each compatibility TPDU within the
    // proven 40-byte CP-DATA envelope and truncate longer viewer input with an
    // explicit diagnostic.
    const int MaximumFirmwareCpDataLength = 40;
    const string ServiceCentreAddress = "1234567890";
    const byte RpDataMobileToNetworkMessageType = 0x00;

    public static byte[] BuildMobileTerminatedCpData(
        string originator,
        string text,
        byte messageReference,
        DateTimeOffset serviceCentreTime)
    {
        var serviceCentreAddress = BuildBcdNumberContents(
            ServiceCentreAddress,
            international: true);
        var tpdu = BuildSmsDeliverTpdu(
            originator,
            text,
            serviceCentreTime);
        List<byte> rpdu =
        [
            0x01,
            messageReference,
            (byte)serviceCentreAddress.Length,
            .. serviceCentreAddress,
            0x00,
            (byte)tpdu.Length,
            .. tpdu,
        ];
        return
        [
            0x09,
            0x01,
            (byte)rpdu.Count,
            .. rpdu,
        ];
    }

    public static (string Text, bool Truncated) FitMobileTerminatedTextForFirmware(
        string originator,
        string text)
    {
        ArgumentNullException.ThrowIfNull(originator);
        var sanitized = SanitizeSmsText(text);
        if (sanitized.Length == 0)
        {
            return (string.Empty, false);
        }

        var count = 1;
        while (count <= sanitized.Length &&
            BuildMobileTerminatedCpData(
                originator,
                sanitized[..count],
                messageReference: 0,
                serviceCentreTime: default).Length <=
                    MaximumFirmwareCpDataLength)
        {
            count++;
        }
        count = Math.Max(1, count - 1);
        return (sanitized[..count], count < sanitized.Length);
    }

    public static byte[] BuildTimestampAndTimeZone(DateTimeOffset localTime) =>
    [
        EncodeTimestampSemiOctet(localTime.Year % 100),
        EncodeTimestampSemiOctet(localTime.Month),
        EncodeTimestampSemiOctet(localTime.Day),
        EncodeTimestampSemiOctet(localTime.Hour),
        EncodeTimestampSemiOctet(localTime.Minute),
        EncodeTimestampSemiOctet(localTime.Second),
        EncodeTimeZone(localTime.Offset),
    ];

    public static bool TryDecodeMobileOriginatedSubmit(
        ReadOnlySpan<byte> cpData,
        out string normalizedDestination,
        out string text,
        out bool international)
    {
        normalizedDestination = "";
        text = "";
        international = false;
        if (!TryGetCpUserData(cpData, out var rpdu) ||
            rpdu.Length < 5 ||
            (rpdu[0] & 0x07) != RpDataMobileToNetworkMessageType)
        {
            return false;
        }

        var offset = 2;
        if (!TrySkipLengthPrefixed(rpdu, ref offset) ||
            !TrySkipLengthPrefixed(rpdu, ref offset) ||
            offset >= rpdu.Length)
        {
            return false;
        }

        var tpduLength = rpdu[offset++];
        return tpduLength >= 7 &&
            offset + tpduLength <= rpdu.Length &&
            TryDecodeSmsSubmitTpdu(
                rpdu.Slice(offset, tpduLength),
                out normalizedDestination,
                out text,
                out international);
    }

    static bool TryDecodeSmsSubmitTpdu(
        ReadOnlySpan<byte> tpdu,
        out string normalizedDestination,
        out string text,
        out bool international)
    {
        normalizedDestination = "";
        text = "";
        international = false;
        if (!GsmSmsSubmit.TryRead(tpdu, out var submit))
        {
            return false;
        }

        normalizedDestination = submit.Destination;
        international = submit.International;
        return TryDecodeSubmitUserData(submit, out text);
    }

    static bool TryDecodeSubmitUserData(
        GsmSmsSubmit submit,
        out string text)
    {
        text = "";
        if (submit.Alphabet == 0x00)
        {
            return TryDecodeGsm7UserData(
                submit.UserData,
                submit.UserDataLength,
                out text);
        }
        if (submit.UserDataLength > submit.UserData.Length)
        {
            return false;
        }
        if (submit.Alphabet == 0x08)
        {
            return TryDecodeUcs2UserData(
                submit.UserData[..submit.UserDataLength],
                out text);
        }
        text = DecodeEightBitUserData(
            submit.UserData[..submit.UserDataLength]);
        return true;
    }

    static bool TryDecodeGsm7UserData(
        ReadOnlySpan<byte> userData,
        int septetCount,
        out string text)
    {
        var requiredBytes = (septetCount * 7 + 7) / 8;
        if (septetCount > 160 || requiredBytes > userData.Length)
        {
            text = "";
            return false;
        }

        text = DecodeGsm7(userData[..requiredBytes], septetCount);
        return true;
    }

    static bool TryDecodeUcs2UserData(
        ReadOnlySpan<byte> userData,
        out string text)
    {
        if ((userData.Length & 1) != 0)
        {
            text = "";
            return false;
        }

        text = System.Text.Encoding.BigEndianUnicode.GetString(userData);
        return true;
    }

    static string DecodeEightBitUserData(ReadOnlySpan<byte> userData) =>
        new(userData.ToArray().Select(value =>
            value is >= 0x20 and <= 0x7e
                ? (char)value
                : '\ufffd').ToArray());

    static bool TryGetCpUserData(
        ReadOnlySpan<byte> cpData,
        out ReadOnlySpan<byte> rpdu)
    {
        rpdu = [];
        if (cpData.Length < 4)
        {
            return false;
        }

        var userDataOffset = cpData.Length >= 5 && cpData[2] == 0x01 ? 4 : 3;
        var userDataLength = cpData[userDataOffset - 1];
        if (userDataLength == 0 ||
            cpData.Length < userDataOffset + userDataLength)
        {
            return false;
        }

        rpdu = cpData.Slice(userDataOffset, userDataLength);
        return true;
    }

    static bool TrySkipLengthPrefixed(
        ReadOnlySpan<byte> data,
        ref int offset)
    {
        if (offset >= data.Length)
        {
            return false;
        }

        var length = data[offset++];
        if (offset + length > data.Length)
        {
            return false;
        }

        offset += length;
        return true;
    }

    internal static bool TryDecodeSemiOctets(
        ReadOnlySpan<byte> encoded,
        int digitCount,
        out string digits)
    {
        digits = "";
        if (digitCount is < 0 or > 20 || encoded.Length * 2 < digitCount)
        {
            return false;
        }

        Span<char> decoded = stackalloc char[digitCount];
        for (var index = 0; index < decoded.Length; index++)
        {
            var value = encoded[index / 2];
            var digit = (index & 1) == 0 ? value & 0x0f : value >> 4;
            decoded[index] = (char)('0' + digit);
        }
        digits = new string(decoded);
        if (decoded.IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            digits = "";
            return false;
        }
        return true;
    }

    static string DecodeGsm7(ReadOnlySpan<byte> packed, int septetCount)
    {
        System.Text.StringBuilder text = new(septetCount);
        var extension = false;
        for (var index = 0; index < septetCount; index++)
        {
            var bitOffset = index * 7;
            var byteOffset = bitOffset / 8;
            var shift = bitOffset % 8;
            var septet = packed[byteOffset] >> shift;
            if (shift > 1 && byteOffset + 1 < packed.Length)
            {
                septet |= packed[byteOffset + 1] << (8 - shift);
            }

            septet &= 0x7f;
            switch (extension, septet)
            {
                case (true, _):
                    text.Append(DecodeGsmExtensionCharacter(septet));
                    extension = false;
                    break;
                case (false, 0x1b):
                    extension = true;
                    break;
                default:
                    text.Append(DecodeGsmDefaultAlphabetCharacter(septet));
                    break;
            }
        }

        text.Append(extension ? "\ufffd" : string.Empty);

        return text.ToString();
    }

    static char DecodeGsmExtensionCharacter(int septet) => septet switch
    {
        0x0a => '\f',
        0x14 => '^',
        0x28 => '{',
        0x29 => '}',
        0x2f => '\\',
        0x3c => '[',
        0x3d => '~',
        0x3e => ']',
        0x40 => '|',
        0x65 => '€',
        _ => '\ufffd',
    };

    static char DecodeGsmDefaultAlphabetCharacter(int septet) => septet switch
    {
        0x00 => '@',
        0x01 => '£',
        0x02 => '$',
        0x03 => '¥',
        0x04 => 'è',
        0x05 => 'é',
        0x06 => 'ù',
        0x07 => 'ì',
        0x08 => 'ò',
        0x09 => 'Ç',
        0x0a => '\n',
        0x0b => 'Ø',
        0x0c => 'ø',
        0x0d => '\r',
        0x0e => 'Å',
        0x0f => 'å',
        0x10 => 'Δ',
        0x11 => '_',
        0x12 => 'Φ',
        0x13 => 'Γ',
        0x14 => 'Λ',
        0x15 => 'Ω',
        0x16 => 'Π',
        0x17 => 'Ψ',
        0x18 => 'Σ',
        0x19 => 'Θ',
        0x1a => 'Ξ',
        0x1c => 'Æ',
        0x1d => 'æ',
        0x1e => 'ß',
        0x1f => 'É',
        0x24 => '¤',
        0x40 => '¡',
        0x5b => 'Ä',
        0x5c => 'Ö',
        0x5d => 'Ñ',
        0x5e => 'Ü',
        0x5f => '§',
        0x60 => '¿',
        0x7b => 'ä',
        0x7c => 'ö',
        0x7d => 'ñ',
        0x7e => 'ü',
        0x7f => 'à',
        _ => (char)septet,
    };

    static byte[] BuildSmsDeliverTpdu(
        string originator,
        string text,
        DateTimeOffset serviceCentreTime)
    {
        ArgumentNullException.ThrowIfNull(originator);
        var internationalOriginator = originator.TrimStart().StartsWith('+');
        var sanitizedOriginator = SanitizeDialableAddress(originator);
        var originatorDigits = EncodeSemiOctets(sanitizedOriginator);
        var userData = PackGsm7(SanitizeSmsText(text), out var userDataLength);
        List<byte> tpdu =
        [
            0x04,
            (byte)sanitizedOriginator.Length,
            internationalOriginator ? (byte)0x91 : (byte)0x81,
            .. originatorDigits,
            0x00,
            0x00,
            .. BuildTimestampAndTimeZone(serviceCentreTime),
            userDataLength,
            .. userData,
        ];
        return tpdu.ToArray();
    }

    static string SanitizeDialableAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digits = new string(value.Where(char.IsDigit).Take(20).ToArray());
        return digits.Length != 0
            ? digits
            : throw new ArgumentException(
                "Incoming SMS addresses must contain at least one digit.",
                nameof(value));
    }

    static string SanitizeSmsText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new string(value
            .Take(MaximumIncomingSmsTextSeptets)
            .Select(character => character is >= ' ' and <= '~'
                ? character
                : ' ')
            .ToArray());
        return text;
    }

    static byte[] BuildBcdNumberContents(
        string digits,
        bool international = false)
    {
        var semiOctets = EncodeSemiOctets(SanitizeDialableAddress(digits));
        byte[] contents = new byte[1 + semiOctets.Length];
        contents[0] = international ? (byte)0x91 : (byte)0x81;
        semiOctets.CopyTo(contents.AsSpan(1));
        return contents;
    }

    static byte[] EncodeSemiOctets(string digits)
    {
        byte[] encoded = new byte[(digits.Length + 1) / 2];
        for (var index = 0; index < digits.Length; index++)
        {
            var nibble = digits[index] - '0';
            if ((index & 1) == 0)
            {
                encoded[index / 2] = (byte)nibble;
            }
            else
            {
                encoded[index / 2] |= (byte)(nibble << 4);
            }
        }
        if ((digits.Length & 1) != 0)
        {
            encoded[^1] |= 0xf0;
        }
        return encoded;
    }

    static byte EncodeTimestampSemiOctet(int value)
    {
        value = Math.Clamp(value, 0, 99);
        return (byte)(value / 10 | value % 10 << 4);
    }

    static byte EncodeTimeZone(TimeSpan offset)
    {
        var quarters = (int)Math.Round(
            offset.TotalMinutes / 15,
            MidpointRounding.AwayFromZero);
        quarters = Math.Clamp(quarters, -99, 99);
        var encoded = EncodeTimestampSemiOctet(Math.Abs(quarters));
        return quarters < 0 ? (byte)(encoded | 0x08) : encoded;
    }

    static byte[] PackGsm7(string text, out byte septetCount)
    {
        septetCount = (byte)Math.Min(
            text.Length,
            MaximumIncomingSmsTextSeptets);
        byte[] packed = new byte[(septetCount * 7 + 7) / 8];
        for (var index = 0; index < septetCount; index++)
        {
            var septet = ToGsmDefaultAlphabetSeptet(text[index]);
            var bitOffset = index * 7;
            var byteOffset = bitOffset / 8;
            var shift = bitOffset % 8;
            packed[byteOffset] |= (byte)(septet << shift);
            if (shift > 1 && byteOffset + 1 < packed.Length)
            {
                packed[byteOffset + 1] |= (byte)(septet >> (8 - shift));
            }
        }
        return packed;
    }

    static byte ToGsmDefaultAlphabetSeptet(char value) => value switch
    {
        '@' => 0x00,
        '$' => 0x02,
        '_' => 0x11,
        '[' or '\\' or ']' or '^' or '`' or '{' or '|' or '}' or '~' => 0x20,
        >= ' ' and <= '~' => (byte)value,
        _ => 0x20,
    };
}
