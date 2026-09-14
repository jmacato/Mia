// SPDX-License-Identifier: MIT

using System.Text;

namespace Mia.Emulator.Camera;

/// <summary>
/// Parsed headers from one complete OBEX packet. TS 07.10 segmentation is
/// removed before this layer; OBEX PUT bodies can still span several packets.
/// </summary>
internal sealed class MiaCommuniCamObexHeaders
{
    const byte NameHeader = 0x01;
    const byte ImageHandleHeader = 0x30;
    const byte TypeHeader = 0x42;
    const byte BodyHeader = 0x48;
    const byte EndOfBodyHeader = 0x49;
    const byte ApplicationParametersHeader = 0x4c;
    const byte DescriptionHeader = 0x71;

    MiaCommuniCamObexHeaders()
    {
    }

    public string? Name { get; private set; }

    public string? ImageHandle { get; private set; }

    public byte[]? Type { get; private set; }

    public byte[]? Description { get; private set; }

    public byte[]? Body { get; private set; }

    public bool HasEndBody { get; private set; }

    public byte[]? ApplicationParameters { get; private set; }

    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        int firstHeaderOffset,
        out MiaCommuniCamObexHeaders? headers)
    {
        headers = null;
        if ((uint)firstHeaderOffset > (uint)packet.Length)
        {
            return false;
        }

        var parsedHeaders = new MiaCommuniCamObexHeaders();
        for (int offset = firstHeaderOffset; offset < packet.Length;)
        {
            if (!TryParseHeader(packet, parsedHeaders, ref offset))
            {
                return false;
            }
        }

        headers = parsedHeaders;
        return true;
    }

    static bool TryParseHeader(
        ReadOnlySpan<byte> packet,
        MiaCommuniCamObexHeaders parsedHeaders,
        ref int offset) =>
        (packet[offset] >> 6) switch
        {
            0 or 1 => TryParseVariableHeader(packet, parsedHeaders, ref offset),
            2 => TrySkipFixedHeader(packet, ref offset, 2),
            _ => TrySkipFixedHeader(packet, ref offset, 5),
        };

    static bool TryParseVariableHeader(
        ReadOnlySpan<byte> packet,
        MiaCommuniCamObexHeaders parsedHeaders,
        ref int offset)
    {
        if (!TryGetVariableHeaderLength(packet, offset, out int length))
        {
            return false;
        }
        byte identifier = packet[offset];
        ReadOnlySpan<byte> value = packet.Slice(offset + 3, length - 3);
        if (!parsedHeaders.Assign(identifier, value))
        {
            return false;
        }
        offset += length;
        return true;
    }

    static bool TryGetVariableHeaderLength(
        ReadOnlySpan<byte> packet,
        int offset,
        out int length)
    {
        length = 0;
        if (offset + 3 > packet.Length)
        {
            return false;
        }
        length = packet[offset + 1] << 8 | packet[offset + 2];
        return length >= 3 && offset + length <= packet.Length;
    }

    static bool TrySkipFixedHeader(
        ReadOnlySpan<byte> packet,
        ref int offset,
        int length)
    {
        if (offset + length > packet.Length)
        {
            return false;
        }
        offset += length;
        return true;
    }

    public bool MatchesType(ReadOnlySpan<byte> expected) =>
        Type is not null && Type.AsSpan().SequenceEqual(expected);

    bool Assign(byte identifier, ReadOnlySpan<byte> value)
        => identifier switch
        {
            NameHeader => TryAssignName(value),
            ImageHandleHeader => TryAssignImageHandle(value),
            TypeHeader => AssignType(value),
            BodyHeader => TryAppendBody(value, endOfBody: false),
            EndOfBodyHeader => TryAppendBody(value, endOfBody: true),
            ApplicationParametersHeader => AssignApplicationParameters(value),
            DescriptionHeader => AssignDescription(value),
            _ => true,
        };

    bool TryAssignName(ReadOnlySpan<byte> value)
    {
        if ((value.Length & 1) != 0)
        {
            return false;
        }
        Name = Encoding.BigEndianUnicode.GetString(value).TrimEnd('\0');
        return true;
    }

    bool TryAssignImageHandle(ReadOnlySpan<byte> value)
    {
        if ((value.Length & 1) != 0)
        {
            return false;
        }
        ImageHandle = Encoding.BigEndianUnicode.GetString(value).TrimEnd('\0');
        return true;
    }

    bool AssignType(ReadOnlySpan<byte> value)
    {
        Type = value.ToArray();
        return true;
    }

    bool TryAppendBody(ReadOnlySpan<byte> value, bool endOfBody)
    {
        if (HasEndBody)
        {
            return false;
        }
        AppendBody(value);
        HasEndBody = endOfBody;
        return true;
    }

    bool AssignApplicationParameters(ReadOnlySpan<byte> value)
    {
        ApplicationParameters = value.ToArray();
        return true;
    }

    bool AssignDescription(ReadOnlySpan<byte> value)
    {
        Description = value.ToArray();
        return true;
    }

    void AppendBody(ReadOnlySpan<byte> value)
    {
        if (Body is null)
        {
            Body = value.ToArray();
            return;
        }

        var combined = new byte[checked(Body.Length + value.Length)];
        Body.CopyTo(combined, 0);
        value.CopyTo(combined.AsSpan(Body.Length));
        Body = combined;
    }
}
