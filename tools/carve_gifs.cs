if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/carve_gifs.cs -- INPUT OUTPUT_DIRECTORY");
    return 2;
}

var input = File.ReadAllBytes(args[0]);
Directory.CreateDirectory(args[1]);
var count = 0;

for (var offset = 0; offset + 13 <= input.Length; offset++)
{
    if (!input.AsSpan(offset, 6).SequenceEqual("GIF87a"u8) &&
        !input.AsSpan(offset, 6).SequenceEqual("GIF89a"u8))
    {
        continue;
    }

    if (!TryFindEnd(input, offset, out var end))
    {
        continue;
    }

    var width = input[offset + 6] | input[offset + 7] << 8;
    var height = input[offset + 8] | input[offset + 9] << 8;
    var path = Path.Combine(args[1], $"gif-{offset:x6}-{width}x{height}.gif");
    File.WriteAllBytes(path, input.AsSpan(offset, end - offset).ToArray());
    Console.WriteLine($"offset=0x{offset:x6} length=0x{end - offset:x} dimensions={width}x{height} path={path}");
    count++;
    offset = end - 1;
}

Console.Error.WriteLine($"carved {count} GIF stream(s)");
return 0;

static bool TryFindEnd(byte[] bytes, int start, out int end)
{
    var cursor = start + 13;
    var packed = bytes[start + 10];
    if ((packed & 0x80) != 0)
    {
        cursor += 3 * (1 << ((packed & 7) + 1));
    }

    while (cursor < bytes.Length)
    {
        switch (bytes[cursor++])
        {
            case 0x3b:
                end = cursor;
                return true;
            case 0x21:
                if (cursor >= bytes.Length)
                {
                    end = 0;
                    return false;
                }
                cursor++;
                if (!SkipSubBlocks(bytes, ref cursor))
                {
                    end = 0;
                    return false;
                }
                break;
            case 0x2c:
                if (cursor + 9 > bytes.Length)
                {
                    end = 0;
                    return false;
                }
                var imagePacked = bytes[cursor + 8];
                cursor += 9;
                if ((imagePacked & 0x80) != 0)
                {
                    cursor += 3 * (1 << ((imagePacked & 7) + 1));
                }
                if (cursor >= bytes.Length)
                {
                    end = 0;
                    return false;
                }
                cursor++;
                if (!SkipSubBlocks(bytes, ref cursor))
                {
                    end = 0;
                    return false;
                }
                break;
            default:
                end = 0;
                return false;
        }
    }

    end = 0;
    return false;
}

static bool SkipSubBlocks(byte[] bytes, ref int cursor)
{
    while (cursor < bytes.Length)
    {
        var length = bytes[cursor++];
        if (length == 0)
        {
            return true;
        }
        if (cursor + length > bytes.Length)
        {
            return false;
        }
        cursor += length;
    }
    return false;
}
