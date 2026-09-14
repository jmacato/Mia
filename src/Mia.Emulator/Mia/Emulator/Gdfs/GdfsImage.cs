// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Mia.Emulator.Gdfs;

internal static class GdfsImage
{
    public const int HeaderLength = 20;
    public const int UfsXorPeriod = 1024;
    public const int RawAddress = 0x580000;
    public const int RawLength = 0x280000;

    /// <summary>
    /// Decodes any supported service-image container into the complete raw
    /// GDFS window. Regions omitted by a partial S-record image retain the
    /// NOR erased value rather than borrowing bytes from another source.
    /// </summary>
    public static byte[] DecodeRaw(ReadOnlySpan<byte> image)
    {
        var program = new byte[RawAddress + RawLength];
        Array.Fill(program, (byte)0xff);
        Load(image, program);
        return program.AsSpan(RawAddress, RawLength).ToArray();
    }

    public static (int Address, int Length) Load(ReadOnlySpan<byte> image, Span<byte> program) =>
        image.StartsWith("S003"u8)
            ? LoadSbn(image, program)
            : image.Length == RawLength
                ? LoadRaw(image, program)
                : LoadBig(image, program);

    public static (int Address, int Length) LoadRaw(ReadOnlySpan<byte> image, Span<byte> program)
    {
        if (image.Length != RawLength)
        {
            throw new InvalidDataException($"Raw GDFS image must be exactly 0x{RawLength:x} bytes.");
        }
        if (RawAddress + image.Length > program.Length)
        {
            throw new InvalidDataException("Raw GDFS image lies outside program flash.");
        }

        image.CopyTo(program[RawAddress..]);
        return (RawAddress, image.Length);
    }

    public static (int Address, int Length) LoadBig(ReadOnlySpan<byte> image, Span<byte> program)
    {
        if (image.Length < HeaderLength)
        {
            throw new InvalidDataException("GDFS .big image is shorter than its 20-byte header.");
        }

        var outerAddress = BinaryPrimitives.ReadInt32LittleEndian(image);
        var outerLength = BinaryPrimitives.ReadInt32LittleEndian(image[4..]);
        var address = BinaryPrimitives.ReadInt32LittleEndian(image[8..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(image[12..]);
        if (outerAddress != address || outerLength != length + 12 || image.Length != outerLength + 8)
        {
            throw new InvalidDataException("GDFS .big container lengths or flash addresses are inconsistent.");
        }
        if (address < 0 || length < 0 || (long)address + length > program.Length)
        {
            throw new InvalidDataException("GDFS .big payload lies outside program flash.");
        }

        var payload = image.Slice(HeaderLength, length);
        var key = RecoverUfsXorKey(payload);
        for (var offset = 0; offset < payload.Length; offset++)
        {
            program[address + offset] = (byte)(payload[offset] ^ key[offset % key.Length]);
        }
        return (address, length);
    }

    public static (int Address, int Length) LoadSbn(ReadOnlySpan<byte> image, Span<byte> program)
    {
        if (!image.StartsWith("S003"u8))
        {
            throw new InvalidDataException("GDFS .sbn image is missing its S003 magic.");
        }

        var cursor = 4;
        var firstAddress = int.MaxValue;
        var lastAddress = 0;
        var recordCount = 0;
        while (cursor < image.Length)
        {
            if (TryConsumeTerminator(image, ref cursor))
            {
                break;
            }

            ValidateRecordMarker(image, cursor);
            int addressLength = DecodeAddressLength(image[cursor + 1], cursor);
            var count = image[cursor + 2];
            ValidateRecordLength(image, cursor, count, addressLength);
            ReadOnlySpan<byte> body = image.Slice(cursor + 3, count);
            ValidateRecordChecksum(body, count, cursor);
            int address = ReadRecordAddress(body, addressLength);
            ReadOnlySpan<byte> payload =
                body.Slice(addressLength, count - addressLength - 1);
            ValidatePayloadRange(address, payload.Length, program.Length);
            payload.CopyTo(program[address..]);
            firstAddress = Math.Min(firstAddress, address);
            lastAddress = Math.Max(lastAddress, address + payload.Length);
            recordCount++;
            cursor += 3 + count;
        }

        if (cursor != image.Length || recordCount == 0)
        {
            throw new InvalidDataException("GDFS .sbn contains no data records or has a malformed terminator.");
        }
        return (firstAddress, lastAddress - firstAddress);
    }

    static bool TryConsumeTerminator(ReadOnlySpan<byte> image, ref int cursor)
    {
        if (image.Length - cursor != 2 ||
            image[cursor] != 'S' ||
            image[cursor + 1] != '8')
        {
            return false;
        }

        cursor += 2;
        return true;
    }

    static void ValidateRecordMarker(ReadOnlySpan<byte> image, int cursor)
    {
        if (image.Length - cursor < 3 || image[cursor] != 'S')
        {
            throw new InvalidDataException(
                $"GDFS .sbn has an invalid record marker at offset 0x{cursor:x}.");
        }
    }

    static int DecodeAddressLength(byte recordType, int cursor) => recordType switch
    {
        (byte)'1' => 2,
        (byte)'2' => 3,
        (byte)'3' => 4,
        _ => throw new InvalidDataException(
            $"GDFS .sbn has unsupported record S{(char)recordType} at offset 0x{cursor:x}."),
    };

    static void ValidateRecordLength(
        ReadOnlySpan<byte> image,
        int cursor,
        int count,
        int addressLength)
    {
        if (count < addressLength + 1 || cursor + 3 + count > image.Length)
        {
            throw new InvalidDataException(
                $"GDFS .sbn has a truncated record at offset 0x{cursor:x}.");
        }
    }

    static void ValidateRecordChecksum(
        ReadOnlySpan<byte> body,
        int count,
        int cursor)
    {
        var sum = count;
        foreach (byte value in body)
        {
            sum += value;
        }
        if ((sum & 0xff) != 0xff)
        {
            throw new InvalidDataException(
                $"GDFS .sbn record at offset 0x{cursor:x} has an invalid checksum.");
        }
    }

    static int ReadRecordAddress(ReadOnlySpan<byte> body, int addressLength)
    {
        var address = 0;
        for (var index = 0; index < addressLength; index++)
        {
            address = checked(address << 8 | body[index]);
        }
        return address;
    }

    static void ValidatePayloadRange(int address, int length, int programLength)
    {
        if (address < 0 || (long)address + length > programLength)
        {
            throw new InvalidDataException(
                "GDFS .sbn payload lies outside program flash.");
        }
    }

    static byte[] RecoverUfsXorKey(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < UfsXorPeriod * 2)
        {
            throw new InvalidDataException("GDFS .big payload is too short to recover its UFS XOR stream.");
        }

        var key = new byte[UfsXorPeriod];
        for (var column = 0; column < key.Length; column++)
        {
            // UFS .big files XOR each payload with a per-file repeating stream.
            // In known full GDFS images, erased 0xff flash is a strict majority
            // in every stream column, making the corresponding key byte exact.
            var counts = new int[256];
            var samples = 0;
            for (var offset = column; offset < payload.Length; offset += key.Length)
            {
                counts[payload[offset]]++;
                samples++;
            }

            var modalCount = counts.Max();
            if (modalCount * 2 <= samples)
            {
                throw new InvalidDataException("GDFS .big payload does not contain the expected erased-flash majority.");
            }
            key[column] = (byte)(Array.IndexOf(counts, modalCount) ^ 0xff);
        }
        return key;
    }
}
