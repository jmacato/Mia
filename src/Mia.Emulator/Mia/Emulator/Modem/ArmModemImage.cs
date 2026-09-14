// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Mia.Emulator.Modem;

internal sealed class ArmModemImage
{
    public const int HeaderLength = 8;

    ArmModemImage(
        uint loadAddress,
        int payloadLength,
        byte[] payload)
    {
        LoadAddress = loadAddress;
        Payload = payload;
        PayloadLength = payloadLength;
    }

    public uint LoadAddress { get; }

    public byte[] Payload { get; }

    public int PayloadLength { get; }

    public static ArmModemImage ParseBih(ReadOnlySpan<byte> image)
    {
        var metadata = ReadMetadata(image);
        return new ArmModemImage(
            metadata.LoadAddress,
            metadata.PayloadLength,
            image[HeaderLength..].ToArray());
    }

    internal static ArmModemImage ParseBihMetadata(ReadOnlySpan<byte> image)
    {
        var metadata = ReadMetadata(image);
        return new ArmModemImage(
            metadata.LoadAddress,
            metadata.PayloadLength,
            []);
    }

    static (uint LoadAddress, int PayloadLength) ReadMetadata(
        ReadOnlySpan<byte> image)
    {
        if (image.Length < HeaderLength)
        {
            throw new InvalidDataException("BIH image is shorter than its eight-byte header.");
        }

        uint loadAddress = BinaryPrimitives.ReadUInt32LittleEndian(image[..4]);
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(4, 4));
        int actualLength = image.Length - HeaderLength;
        if (declaredLength != actualLength)
        {
            throw new InvalidDataException(
                $"BIH header declares 0x{declaredLength:x} payload bytes, " +
                $"but the image contains 0x{actualLength:x}.");
        }

        return (loadAddress, actualLength);
    }
}
