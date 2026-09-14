using System.Security.Cryptography;

const byte DisplayAddress = 0x72;
const byte ScanRowCommand = 0x8c;
const int Width = 101;
const int Height = 80;
const byte DefaultColumnStart = 0x1b;
const byte DefaultColumnEnd = 0x7f;
const byte DefaultRowStart = 0x00;
const byte DefaultRowEnd = 0x4f;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/extract_lcd_frames.cs -- TRANSACTIONS.bin OUTPUT_DIR");
    return 2;
}

var capturePath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var capture = File.ReadAllBytes(capturePath);
Directory.CreateDirectory(outputDirectory);

var registers = new byte[256];
var framebuffer = new byte[Width * Height];
registers[0x04] = DefaultColumnStart;
registers[0x05] = DefaultRowStart;
registers[0x07] = DefaultColumnStart;
registers[0x08] = DefaultColumnEnd;
registers[0x09] = DefaultRowStart;
registers[0x0a] = DefaultRowEnd;

var offset = 0;
var transaction = 0;
var frame = 0;
var totalPixels = 0L;
Console.WriteLine("frame\ttransaction\tpixels\trgb332-sha256");
while (offset < capture.Length)
{
    if (capture.Length - offset < 3)
    {
        throw new InvalidDataException($"Truncated transaction header at 0x{offset:x}.");
    }

    var address = capture[offset++];
    var length = capture[offset++] | capture[offset++] << 8;
    if (capture.Length - offset < length)
    {
        throw new InvalidDataException(
            $"Transaction {transaction + 1} declares {length} payload bytes " +
            $"with only {capture.Length - offset} remaining.");
    }

    transaction++;
    var payload = capture.AsSpan(offset, length);
    offset += length;
    if (address != DisplayAddress || payload.IsEmpty)
    {
        continue;
    }

    if (payload[0] != ScanRowCommand)
    {
        for (var index = 0; index + 1 < payload.Length; index += 2)
        {
            registers[payload[index]] = payload[index + 1];
        }
        continue;
    }

    var framePixels = 0;
    foreach (var pixel in payload[1..])
    {
        var columnStart = registers[0x07];
        var columnEnd = registers[0x08];
        var rowStart = registers[0x09];
        var rowEnd = registers[0x0a];
        var column = registers[0x04];
        var row = registers[0x05];
        var x = column - columnStart;
        var y = row - rowStart;
        if (column >= columnStart && column <= columnEnd &&
            row >= rowStart && row <= rowEnd && x < Width && y < Height)
        {
            framebuffer[y * Width + x] = pixel;
            framePixels++;
            totalPixels++;
        }

        if (column < columnEnd)
        {
            registers[0x04] = (byte)(column + 1);
        }
        else
        {
            registers[0x04] = columnStart;
            registers[0x05] = row < rowEnd ? (byte)(row + 1) : rowStart;
        }
    }

    if (framePixels == 0)
    {
        continue;
    }

    frame++;
    var stem = Path.Combine(outputDirectory, $"frame-{frame:000}");
    File.WriteAllBytes(stem + ".rgb332", framebuffer);
    WritePpm(stem + ".ppm", framebuffer);
    Console.WriteLine(
        $"{frame}\t{transaction}\t{framePixels}\t{Convert.ToHexStringLower(SHA256.HashData(framebuffer))}");
}

Console.Error.WriteLine(
    $"Extracted {frame} pixel-bearing frame(s), {totalPixels:n0} pixel write(s), " +
    $"from {transaction} transaction(s).");
return 0;

static void WritePpm(string path, ReadOnlySpan<byte> framebuffer)
{
    using var output = File.Create(path);
    using (var header = new StreamWriter(output, leaveOpen: true))
    {
        header.Write($"P6\n{Width} {Height}\n255\n");
    }

    Span<byte> rgb = stackalloc byte[3];
    foreach (var pixel in framebuffer)
    {
        rgb[0] = (byte)((pixel >> 5) * 255 / 7);
        rgb[1] = (byte)(((pixel >> 2) & 7) * 255 / 7);
        rgb[2] = (byte)((pixel & 3) * 255 / 3);
        output.Write(rgb);
    }
}
