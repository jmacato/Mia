#!/usr/bin/env dotnet

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/find_u24.cs -- FILE HEX_VALUE...");
    return 2;
}

byte[] bytes = File.ReadAllBytes(args[0]);
foreach (string text in args.Skip(1))
{
    int value = Convert.ToInt32(text, 16);
    if ((uint)value > 0x00ff_ffff)
    {
        throw new ArgumentOutOfRangeException(
            nameof(args),
            $"0x{value:x} does not fit in 24 bits.");
    }

    Console.WriteLine($"0x{value:x6}:");
    for (int offset = 0; offset <= bytes.Length - 3; offset++)
    {
        int candidate =
            bytes[offset] |
            bytes[offset + 1] << 8 |
            bytes[offset + 2] << 16;
        if (candidate == value)
        {
            Console.WriteLine(
                $"  file+0x{offset:x8} code-word=0x{offset / 2:x6}");
        }
    }
}

return 0;
