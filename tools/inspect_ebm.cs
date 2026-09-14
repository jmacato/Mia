if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/inspect_ebm.cs -- INPUT [OUTPUT_DIRECTORY]");
    return 2;
}

var bytes = File.ReadAllBytes(args[0]);
var magic = ".ebm\0"u8;
var outputDirectory = args.Length == 2 ? args[1] : null;
if (outputDirectory is not null)
{
    Directory.CreateDirectory(outputDirectory);
}

for (var offset = 0; offset + magic.Length <= bytes.Length; offset++)
{
    if (!bytes.AsSpan(offset, magic.Length).SequenceEqual(magic))
    {
        continue;
    }

    if (offset + 0x310 > bytes.Length)
    {
        continue;
    }

    var width = bytes[offset + 0x0c] + 1;
    var height = bytes[offset + 0x0d] + 1;
    var compressedLength = bytes[offset + 0x30e] | bytes[offset + 0x30f] << 8;
    var dataStart = offset + 0x310;
    var dataEnd = dataStart + compressedLength;
    if (width > 256 || height > 256 || compressedLength == 0 || dataEnd > bytes.Length)
    {
        continue;
    }

    var decoded = DecodeRuns(bytes.AsSpan(dataStart, compressedLength));
    if (decoded.Count != width * height)
    {
        continue;
    }

    Console.WriteLine($"offset=0x{offset:x} dimensions={width}x{height} compressed=0x{compressedLength:x} end=0x{dataEnd:x}");
    if (outputDirectory is not null)
    {
        WritePpm(
            Path.Combine(outputDirectory, $"ebm-{offset:x6}-{width}x{height}.ppm"),
            width,
            height,
            bytes.AsSpan(offset + 0x0e, 256 * 3),
            decoded);
    }
}

return 0;

static List<byte> DecodeRuns(ReadOnlySpan<byte> encoded)
{
    var decoded = new List<byte>();
    for (var cursor = 0; cursor < encoded.Length; cursor++)
    {
        var value = encoded[cursor];
        if (value < 0x80)
        {
            decoded.Add(value);
            continue;
        }

        if (++cursor >= encoded.Length)
        {
            break;
        }
        for (var count = (value & 0x7f) + 1; count > 0; count--)
        {
            decoded.Add(encoded[cursor]);
        }
    }
    return decoded;
}

static void WritePpm(
    string path,
    int width,
    int height,
    ReadOnlySpan<byte> palette,
    IReadOnlyList<byte> pixels)
{
    using var output = File.Create(path);
    using var writer = new BinaryWriter(output);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    foreach (var pixel in pixels)
    {
        writer.Write(palette[pixel * 3]);
        writer.Write(palette[pixel * 3 + 1]);
        writer.Write(palette[pixel * 3 + 2]);
    }
}
