#!/usr/bin/env dotnet

// Temporary ARM literal-pool annotation probe.

using System.Buffers.Binary;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/annotate_arm_literals.cs -- ASM FIRMWARE");
    return 2;
}

byte[] firmware = File.ReadAllBytes(args[1]);
var literalPattern = new Regex(@"\[0x(?<literal>[0-9a-fA-F]{8})\]");
foreach (string line in File.ReadLines(args[0]))
{
    Match match = literalPattern.Match(line);
    if (!match.Success)
    {
        continue;
    }

    uint literal = Convert.ToUInt32(match.Groups["literal"].Value, 16);
    long offset = literal - 0x01000000L;
    if (offset < 0 || offset + 4 > firmware.Length)
    {
        continue;
    }

    uint value = BinaryPrimitives.ReadUInt32LittleEndian(firmware.AsSpan((int)offset, 4));
    Console.WriteLine($"{line}  => 0x{value:x8}");
}

return 0;
