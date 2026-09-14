#!/usr/bin/env dotnet

// SPDX-License-Identifier: MIT

using System.Globalization;

if (args.Length != 2 ||
    !ushort.TryParse(
        args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture,
        out ushort searchedSignal))
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/find_avr_signal_dispatchers.cs -- " +
        "FIRMWARE.bin SIGNAL_HEX");
    return 2;
}

byte[] firmware = File.ReadAllBytes(args[0]);
for (int offset = 0; offset <= firmware.Length - 7; offset++)
{
    int cursor = offset;
    uint value = ReadBigEndian16();
    byte flags = Read8();
    if ((flags & 1) == 0 || (flags & ~7) != 0 || value > searchedSignal)
    {
        continue;
    }

    uint defaultTarget = Read24();
    if (!IsCodeTarget(defaultTarget))
    {
        continue;
    }

    bool relativeTargets = (flags & 2) != 0;
    bool wideRelativeTargets = (flags & 4) != 0;
    bool found = false;
    bool valid = true;
    int entries = 0;
    while (cursor < firmware.Length &&
           cursor - offset <= 4096 &&
           entries++ < 512)
    {
        uint target;
        if (relativeTargets)
        {
            if (!TryReadRelative(out uint displacement))
            {
                valid = false;
                break;
            }
            if (displacement > defaultTarget)
            {
                valid = false;
                break;
            }
            target = defaultTarget - displacement;
        }
        else
        {
            if (cursor > firmware.Length - 3)
            {
                valid = false;
                break;
            }
            target = Read24();
        }
        if (!IsCodeTarget(target))
        {
            valid = false;
            break;
        }
        if (value == searchedSignal)
        {
            found = true;
        }

        if (cursor >= firmware.Length)
        {
            valid = false;
            break;
        }
        byte marker = Read8();
        if (marker == 0xfb)
        {
            break;
        }

        if (!TryReadDelta(marker, out uint delta) ||
            delta == 0 ||
            value + delta > ushort.MaxValue)
        {
            valid = false;
            break;
        }
        value += delta;
        if (value > searchedSignal && !found)
        {
            valid = false;
            break;
        }
    }

    if (valid && found)
    {
        Console.WriteLine(
            $"table=0x{offset:x6} flags=0x{flags:x2} " +
            $"default=0x{defaultTarget:x6} entries={entries}");
    }

    byte Read8() => firmware[cursor++];

    ushort ReadBigEndian16()
    {
        byte high = Read8();
        return (ushort)(high << 8 | Read8());
    }

    uint ReadBigEndian32()
    {
        ushort high = ReadBigEndian16();
        return (uint)high << 16 | ReadBigEndian16();
    }

    uint Read24()
    {
        byte low = Read8();
        byte middle = Read8();
        return low | (uint)middle << 8 | (uint)Read8() << 16;
    }

    bool TryReadRelative(out uint displacement)
    {
        displacement = 0;
        int length = wideRelativeTargets ? 2 : 1;
        if (cursor > firmware.Length - length)
        {
            return false;
        }
        displacement = Read8();
        if (wideRelativeTargets)
        {
            displacement |= (uint)Read8() << 8;
        }
        return true;
    }

    bool TryReadDelta(byte marker, out uint delta)
    {
        delta = 0;
        int length = marker switch
        {
            0xff => 1,
            0xfe => 2,
            0xfd => 4,
            _ => 0,
        };
        if (cursor > firmware.Length - length)
        {
            return false;
        }
        delta = marker switch
        {
            0xff => Read8(),
            0xfe => ReadBigEndian16(),
            0xfd => ReadBigEndian32(),
            _ => marker,
        };
        return true;
    }
}

return 0;

static bool IsCodeTarget(uint target) =>
    target is > 0 and < 0x400000;
