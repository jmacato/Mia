using System.Globalization;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: dotnet run tools/find_avr_send_destinations.cs -- FIRMWARE SEND_TARGET_WORD DESTINATION_BYTE [MAX_WORD_GAP]");
    return 2;
}

var firmware = File.ReadAllBytes(args[0]);
var sendTarget = ParseHex(args[1]);
var destination = (byte)ParseHex(args[2]);
var maxWordGap = args.Length >= 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 24;
var words = firmware.Length / 2;
var matches = 0;

for (var word = 0; word < words; word++)
{
    var opcode = GetWord(word);
    if (!IsLdi(opcode, 20, destination))
    {
        continue;
    }

    var limit = Math.Min(words - 1, word + maxWordGap);
    for (var candidate = word + 1; candidate <= limit; candidate++)
    {
        var candidateOpcode = GetWord(candidate);
        if (!TryGetControlTarget(candidate, candidateOpcode, out var target, out var mnemonic))
        {
            continue;
        }
        if (target == sendTarget)
        {
            Console.WriteLine($"destination-load=0x{word:x6} {mnemonic}=0x{candidate:x6} gap={candidate - word}");
            matches++;
            break;
        }
        if (mnemonic is "JMP" or "RJMP" or "RET")
        {
            break;
        }
    }
}

Console.WriteLine($"destination=0x{destination:x2}: {matches} candidate send site(s)");
return 0;

bool TryGetControlTarget(int word, int opcode, out int target, out string mnemonic)
{
    var isCall = (opcode & 0xfe0e) == 0x940e;
    var isJump = (opcode & 0xfe0e) == 0x940c;
    var isRelativeCall = (opcode & 0xf000) == 0xd000;
    var isRelativeJump = (opcode & 0xf000) == 0xc000;
    if (opcode == 0x9508)
    {
        target = -1;
        mnemonic = "RET";
        return true;
    }
    if (!isCall && !isJump && !isRelativeCall && !isRelativeJump)
    {
        target = 0;
        mnemonic = "";
        return false;
    }

    target = isRelativeCall || isRelativeJump
        ? word + 1 + SignExtend12(opcode & 0x0fff)
        : GetWord(word + 1) | ((opcode & 1) << 16) | ((opcode & 0x1f0) << 13);
    mnemonic = isCall ? "CALL" : isJump ? "JMP" : isRelativeCall ? "RCALL" : "RJMP";
    return true;
}

bool IsLdi(int opcode, int register, byte value) =>
    (opcode & 0xf000) == 0xe000 &&
    16 + ((opcode >> 4) & 0x0f) == register &&
    (byte)((opcode & 0x0f) | ((opcode >> 4) & 0xf0)) == value;

int GetWord(int word) => firmware[word * 2] | firmware[word * 2 + 1] << 8;

int SignExtend12(int value) => (value & 0x800) != 0 ? value - 0x1000 : value;

int ParseHex(string value) => int.Parse(
    value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
    NumberStyles.HexNumber,
    CultureInfo.InvariantCulture);
