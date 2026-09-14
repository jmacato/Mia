#!/usr/bin/env dotnet

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/find_u32.cs -- FILE HEX_VALUE...");
    return 2;
}

byte[] bytes = File.ReadAllBytes(args[0]);
foreach (string text in args.Skip(1))
{
    uint value = Convert.ToUInt32(text, 16);
    Console.WriteLine($"0x{value:x8}:");
    for (int offset = 0; offset <= bytes.Length - sizeof(uint); offset++)
    {
        if (BitConverter.ToUInt32(bytes, offset) == value)
        {
            Console.WriteLine($"  file+0x{offset:x8} image=0x{offset + 0x01000000L:x8}");
        }
    }
}

return 0;
