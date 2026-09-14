// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemImageTests
{
    [Fact]
    public void ParsesDeclaredLoadAddressAndPayload()
    {
        byte[] image = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x01000000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 4);
        byte[] payload = [0xde, 0xad, 0xbe, 0xef];
        payload.CopyTo(image, 8);

        ArmModemImage parsed = ArmModemImage.ParseBih(image);

        Assert.Equal(0x01000000u, parsed.LoadAddress);
        Assert.Equal([0xde, 0xad, 0xbe, 0xef], parsed.Payload);
        Assert.Equal(payload.Length, parsed.PayloadLength);
    }

    [Fact]
    public void RejectsPayloadLengthMismatch()
    {
        byte[] image = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x01000000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 5);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ArmModemImage.ParseBih(image));

        Assert.Contains("declares 0x5", error.Message, StringComparison.Ordinal);
        Assert.Contains("contains 0x4", error.Message, StringComparison.Ordinal);
    }
}
