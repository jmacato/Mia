// SPDX-License-Identifier: MIT

using System.Globalization;

if (args.Length != 2 ||
    !int.TryParse(args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture,
        out var offset))
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/decode_avr_dispatch_table.cs -- FIRMWARE.bin TABLE_BYTE_OFFSET_HEX");
    return 2;
}

var firmware = File.ReadAllBytes(args[0]);
if ((uint)offset > firmware.Length - 6)
{
    Console.Error.WriteLine($"Table header at 0x{offset:x} is outside {args[0]}.");
    return 2;
}

var cursor = offset;
var firstValue = ReadBigEndian16();
var flags = Read8();
var defaultTarget = Read24();
var sparseValues = (flags & 0x01) != 0;
var relativeTargets = (flags & 0x02) != 0;
var wideRelativeTargets = (flags & 0x04) != 0;

if ((flags & ~0x07) != 0)
{
    Console.Error.WriteLine(
        $"Dispatch table at 0x{offset:x} has unknown flags 0x{flags & ~0x07:x2}.");
    return 2;
}

Console.WriteLine(
    $"table=0x{offset:x} first=0x{firstValue:x4} flags=0x{flags:x2} default=0x{defaultTarget:x6}");

if (!sparseValues)
{
    Console.Error.WriteLine("Only sparse-value dispatch tables are currently decoded.");
    return 2;
}

var value = (uint)firstValue;
while (true)
{
    uint target;
    if (relativeTargets)
    {
        var displacement = wideRelativeTargets ? ReadLittleEndian16() : Read8();
        target = defaultTarget - displacement;
    }
    else
    {
        target = Read24();
    }
    Console.WriteLine($"0x{value:x4} -> 0x{target:x6}");

    var marker = Read8();
    if (marker == 0xfb)
        break;

    uint delta = marker switch
    {
        0xff => Read8(),
        0xfe => ReadBigEndian16(),
        0xfd => ReadBigEndian32(),
        _ => marker,
    };
    value += delta;
}

return 0;

byte Read8()
{
    if ((uint)cursor >= firmware.Length)
        throw new EndOfStreamException($"Dispatch table at 0x{offset:x} is truncated.");
    return firmware[cursor++];
}

ushort ReadLittleEndian16()
{
    var low = Read8();
    return (ushort)(low | Read8() << 8);
}

uint Read24()
{
    var low = ReadLittleEndian16();
    return low | (uint)Read8() << 16;
}

ushort ReadBigEndian16()
{
    var high = Read8();
    return (ushort)(high << 8 | Read8());
}

uint ReadBigEndian32()
{
    var high = ReadBigEndian16();
    return (uint)high << 16 | ReadBigEndian16();
}
