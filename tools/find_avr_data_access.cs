// Run with: dotnet run tools/find_avr_data_access.cs -- flat.bin ADDRESS|START-END...

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/find_avr_data_access.cs -- IMAGE ADDRESS|START-END...");
    return 2;
}

var bytes = File.ReadAllBytes(args[0]);
var targets = args[1..]
    .SelectMany(ParseTarget)
    .ToHashSet();

for (var wordAddress = 0; wordAddress + 1 < bytes.Length / 2; wordAddress++)
{
    var opcode = GetWord(wordAddress);
    if ((opcode & 0xfc0f) is not (0x9000 or 0x9200))
    {
        continue;
    }

    var address = GetWord(wordAddress + 1);
    if (!targets.Contains(address))
    {
        continue;
    }

    var register = (opcode >> 4) & 0x1f;
    var operation = (opcode & 0x0200) == 0 ? "LDS" : "STS";
    Console.WriteLine(
        $"{operation} r{register}, 0x{address:x4} at word 0x{wordAddress:x6} (byte 0x{wordAddress * 2:x6})");
}

return 0;

ushort GetWord(int wordAddress) =>
    (ushort)(bytes[wordAddress * 2] | bytes[wordAddress * 2 + 1] << 8);

static IEnumerable<int> ParseTarget(string value)
{
    var separator = value.IndexOf('-');
    if (separator < 0)
    {
        yield return Convert.ToInt32(value, 16);
        yield break;
    }

    var first = Convert.ToInt32(value[..separator], 16);
    var last = Convert.ToInt32(value[(separator + 1)..], 16);
    if (first > last)
    {
        throw new ArgumentException($"Invalid descending address range: {value}");
    }

    for (var address = first; address <= last; address++)
    {
        yield return address;
    }
}
