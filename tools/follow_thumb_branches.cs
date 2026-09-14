#!/usr/bin/env dotnet

// SPDX-License-Identifier: MIT

const uint ImageBase = 0x01000000;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/follow_thumb_branches.cs -- MODEM.bin ADDRESS");
    return 2;
}

byte[] firmware = File.ReadAllBytes(args[0]);
uint address = Convert.ToUInt32(args[1], 0x10);
var visited = new HashSet<uint>();

for (int depth = 0; depth < 256; depth++)
{
    if (!visited.Add(address))
    {
        Console.WriteLine($"0x{address:x8}: branch loop");
        return 1;
    }

    int offset = checked((int)(address - ImageBase));
    ushort instruction = BitConverter.ToUInt16(firmware, offset);
    if ((instruction & 0xf800) != 0xe000)
    {
        Console.WriteLine(
            $"0x{address:x8}: 0x{instruction:x4} is not an unconditional Thumb branch");
        return 0;
    }

    int displacement = instruction & 0x07ff;
    if ((displacement & 0x0400) != 0)
    {
        displacement |= unchecked((int)0xfffff800);
    }

    uint target = unchecked(address + 4u + (uint)(displacement << 1));
    Console.WriteLine($"0x{address:x8}: 0x{instruction:x4} -> 0x{target:x8}");
    address = target;
}

Console.Error.WriteLine("Branch chain exceeded 256 instructions.");
return 1;
