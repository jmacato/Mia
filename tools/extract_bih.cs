using System.Buffers.Binary;
using System.Security.Cryptography;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/extract_bih.cs -- INPUT.bih OUTPUT.bin");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
if (inputPath == outputPath)
{
    Console.Error.WriteLine("Input and output paths must differ.");
    return 2;
}

using FileStream input = File.OpenRead(inputPath);
if (input.Length < 8)
{
    Console.Error.WriteLine("BIH file is shorter than its eight-byte header.");
    return 1;
}

Span<byte> header = stackalloc byte[8];
input.ReadExactly(header);
uint loadAddress = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
long expectedLength = 8L + payloadLength;
if (input.Length != expectedLength)
{
    Console.Error.WriteLine(
        $"BIH length mismatch: header declares 0x{payloadLength:x} payload bytes " +
        $"but file contains 0x{input.Length - 8:x}.");
    return 1;
}

using (FileStream output = File.Create(outputPath))
{
    input.CopyTo(output);
}

using FileStream payload = File.OpenRead(outputPath);
string sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
Console.WriteLine($"load-address=0x{loadAddress:x8}");
Console.WriteLine($"payload-length=0x{payloadLength:x8}");
Console.WriteLine($"payload-sha256={sha256}");
return 0;
