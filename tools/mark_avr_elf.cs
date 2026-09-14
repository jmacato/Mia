if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run tools/mark_avr_elf.cs -- FILE.elf");
    return 2;
}

var path = args[0];
var bytes = File.ReadAllBytes(path);
if (bytes.Length < 20 ||
    bytes[0] != 0x7f || bytes[1] != (byte)'E' ||
    bytes[2] != (byte)'L' || bytes[3] != (byte)'F' ||
    bytes[4] != 1 || bytes[5] != 1)
{
    Console.Error.WriteLine($"Not a 32-bit little-endian ELF file: {path}");
    return 2;
}

bytes[18] = 0x53; // ELF e_machine = EM_AVR (83)
bytes[19] = 0x00;
File.WriteAllBytes(path, bytes);
return 0;
