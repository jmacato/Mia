// SPDX-License-Identifier: BSD-3-Clause

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class SwSimCardTests
{
    [Fact]
    public void DirectoryResponseUsesStandardLengthAndDisablesChv1()
    {
        var card = new SwSimCard();

        Assert.Equal(
            new byte[] { 0xa4, 0x9f, 0x17 },
            Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x20));

        byte[] response = Send(card, 0xa0, 0xc0, 0x00, 0x00, 0x17);

        Assert.Equal(0xc0, response[0]);
        Assert.Equal(0x0a, response[13]);
        Assert.Equal(0xb2, response[14]);
        Assert.Equal(new byte[] { 0x90, 0x00 }, response[^2..]);
    }

    [Fact]
    public void PhaseFileRequiresItsDirectoryAndReadsThroughT0()
    {
        var card = new SwSimCard();

        Assert.Equal(
            new byte[] { 0xa4, 0x94, 0x04 },
            Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0xae));

        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x20);
        Assert.Equal(
            new byte[] { 0xa4, 0x9f, 0x0f },
            Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0xae));
        Assert.Equal(
            new byte[] { 0xb0, 0x02, 0x90, 0x00 },
            Send(card, 0xa0, 0xb0, 0x00, 0x00, 0x01));
    }

    [Fact]
    public void LanguagePreferenceAdvertisesEnglishBeforePolish()
    {
        var card = new SwSimCard();
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x20);
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0x05);

        Assert.Equal(
            new byte[] { 0xb0, 0x01, 0x0e, 0xff, 0xff, 0x90, 0x00 },
            Send(card, 0xa0, 0xb0, 0x00, 0x00, 0x04));
    }

    [Fact]
    public void SmsParametersProvideCompatibilityNetworkServiceCentre()
    {
        var card = new SwSimCard();
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x10);
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0x42);

        byte[] response = Send(card, 0xa0, 0xb2, 0x01, 0x04, 0x2c);
        byte[] record = response[1..^2];

        Assert.Equal(44, record.Length);
        Assert.Equal("T68i", System.Text.Encoding.ASCII.GetString(record[..4]));
        Assert.Equal(0xfd, record[16]);
        Assert.All(record[17..29], value => Assert.Equal(0xff, value));
        Assert.Equal(
            new byte[]
            {
                0x04, 0x81, 0x21, 0x43, 0xf5,
                0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            },
            record[29..41]);
        Assert.Equal(new byte[] { 0x90, 0x00 }, response[^2..]);
    }

    [Fact]
    public void UndefinedPcsCompatibilityFileFailsClosed()
    {
        var card = new SwSimCard();
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x40);

        Assert.Equal(
            new byte[] { 0xa4, 0x94, 0x04 },
            Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0x9b));
    }

    [Fact]
    public void RecordUpdatesUseOneProcedureByteAndRemainOnTheCard()
    {
        var card = new SwSimCard();
        var persistenceVersions = new List<long>();
        card.PersistenceChanged += persistenceVersions.Add;
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x7f, 0x10);
        Send(card, 0xa0, 0xa4, 0x00, 0x00, 0x02, 0x6f, 0x42);
        byte[] record = Enumerable.Repeat((byte)0xff, 44).ToArray();
        record[0] = 0x01;
        record[1] = 0x02;

        Assert.Equal(
            new byte[] { 0xdc, 0x90, 0x00 },
            Send(card, [0xa0, 0xdc, 0x01, 0x04, 0x2c, .. record]));

        byte[] read = Send(card, 0xa0, 0xb2, 0x01, 0x04, 0x2c);
        Assert.Equal(0xb2, read[0]);
        Assert.Equal(record, read[1..^2]);
        Assert.Equal(new byte[] { 0x90, 0x00 }, read[^2..]);
        Assert.Equal([1], persistenceVersions);
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x88)]
    public void UnconfiguredSecurityServicesFailClosed(byte instruction)
    {
        var card = new SwSimCard();

        byte[] response = Send(card, 0xa0, instruction, 0x00, 0x00,
            instruction == 0x88 ? (byte)0x10 : (byte)0x08);

        Assert.Equal(new byte[] { 0x98, 0x04 }, response);
    }

    static byte[] Send(SwSimCard card, params byte[] bytes)
    {
        var responseBytes = new List<byte>();
        foreach (byte value in bytes)
        {
            SimCardResponse? response = card.Transmit(value);
            if (response is not null)
            {
                responseBytes.AddRange(response.Value.Data);
            }
        }

        return responseBytes.ToArray();
    }
}
