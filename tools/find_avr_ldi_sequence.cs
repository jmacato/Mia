if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run tools/find_avr_ldi_sequence.cs -- FIRMWARE HEX-BYTES [MAX-WORD-GAP]");
    return 2;
}

var firmware = File.ReadAllBytes(args[0]);
var wanted = Convert.FromHexString(args[1]);
var maxWordGap = args.Length >= 3 ? int.Parse(args[2]) : 16;
var loads = new List<(int Word, int Register, byte Value)>();

for (var offset = 0; offset + 1 < firmware.Length; offset += 2)
{
    var opcode = (ushort)(firmware[offset] | firmware[offset + 1] << 8);
    if ((opcode & 0xf000) != 0xe000)
        continue;

    var register = 16 + ((opcode >> 4) & 0x0f);
    var value = (byte)((opcode & 0x0f) | ((opcode >> 4) & 0xf0));
    loads.Add((offset / 2, register, value));
}

for (var start = 0; start < loads.Count; start++)
{
    if (loads[start].Value != wanted[0])
        continue;

    var matched = new List<(int Word, int Register, byte Value)> { loads[start] };
    var previousWord = loads[start].Word;
    var nextWanted = 1;
    for (var candidate = start + 1; candidate < loads.Count && nextWanted < wanted.Length; candidate++)
    {
        var load = loads[candidate];
        if (load.Word - previousWord > maxWordGap)
            break;
        if (load.Value != wanted[nextWanted])
            continue;

        matched.Add(load);
        previousWord = load.Word;
        nextWanted++;
    }

    if (nextWanted != wanted.Length)
        continue;

    Console.WriteLine(string.Join(" ", matched.Select(load =>
        $"word=0x{load.Word:x6}:r{load.Register}=0x{load.Value:x2}")));
}

return 0;
