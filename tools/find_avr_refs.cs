using System.Globalization;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/find_avr_refs.cs -- FIRMWARE TARGET_WORD [TARGET_WORD ...]");
    return 2;
}

var firmware = File.ReadAllBytes(args[0]);
var targets = args[1..]
    .Select(value => int.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
        NumberStyles.HexNumber, CultureInfo.InvariantCulture))
    .ToHashSet();
var counts = targets.ToDictionary(target => target, _ => 0);

for (var word = 0; word + 1 < firmware.Length / 2; word++)
{
    var opcode = GetWord(firmware, word);
    var isCall = (opcode & 0xfe0e) == 0x940e;
    var isJump = (opcode & 0xfe0e) == 0x940c;
    var isRelativeCall = (opcode & 0xf000) == 0xd000;
    var isRelativeJump = (opcode & 0xf000) == 0xc000;
    if (!isCall && !isJump && !isRelativeCall && !isRelativeJump)
    {
        continue;
    }

    var target = isRelativeCall || isRelativeJump
        ? word + 1 + SignExtend12(opcode & 0x0fff)
        : GetWord(firmware, word + 1) | ((opcode & 1) << 16) | ((opcode & 0x1f0) << 13);
    if (!targets.Contains(target))
    {
        continue;
    }

    counts[target]++;
    var mnemonic = isCall ? "CALL " : isJump ? "JMP  " : isRelativeCall ? "RCALL" : "RJMP ";
    Console.WriteLine($"0x{word:x6} {mnemonic} 0x{target:x6}");
}

foreach (var target in targets.Order())
{
    Console.WriteLine($"0x{target:x6}: {counts[target]} raw absolute reference(s)");
}

return 0;

int GetWord(byte[] bytes, int word) => bytes[word * 2] | bytes[word * 2 + 1] << 8;

int SignExtend12(int value) => (value & 0x800) != 0 ? value - 0x1000 : value;
