// SPDX-License-Identifier: MIT

using System.Globalization;

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Usage: dotnet run tools/inventory_avr_control_flow.cs -- IMAGE [START_BYTE [END_BYTE]]");
    return 2;
}

var image = File.ReadAllBytes(args[0]);
var startByte = args.Length >= 2 ? ParseHex(args[1]) : 0;
var endByte = args.Length >= 3 ? ParseHex(args[2]) : image.Length;
if (startByte < 0 || endByte > image.Length || startByte >= endByte ||
    (startByte & 1) != 0 || (endByte & 1) != 0)
{
    Console.Error.WriteLine("The scan range must be an even, non-empty byte range inside IMAGE.");
    return 2;
}

var references = new List<ControlFlowReference>();
for (var word = startByte / 2; word < endByte / 2; word++)
{
    var opcode = GetWord(image, word);
    if (TryDecodeControlFlow(image, word, opcode, out var target, out var operation))
    {
        references.Add(new ControlFlowReference(word, target, operation));
    }

    if (IsTwoWordInstruction(opcode))
    {
        word++;
    }
}

foreach (var group in references
             .GroupBy(reference => reference.Target)
             .OrderBy(group => group.Key))
{
    var targetByte = group.Key * 2L;
    var location = targetByte >= startByte && targetByte < endByte ? "internal" : "external";
    var calls = group.Count(reference => reference.Operation is "CALL" or "RCALL");
    var jumps = group.Count() - calls;
    var sites = string.Join(' ', group
        .OrderBy(reference => reference.Site)
        .Select(reference => $"0x{reference.Site:x6}:{reference.Operation}"));
    Console.WriteLine(
        $"target-word=0x{group.Key:x6} target-byte=0x{targetByte:x6} {location,-8} " +
        $"calls={calls,3} jumps={jumps,3} sites={sites}");
}

Console.Error.WriteLine(
    $"Found {references.Count:n0} raw direct control-flow candidates to " +
    $"{references.Select(reference => reference.Target).Distinct().Count():n0} targets. " +
    "Treat candidates as code only after dynamic execution evidence.");
return 0;

static bool TryDecodeControlFlow(
    byte[] image,
    int word,
    int opcode,
    out int target,
    out string operation)
{
    var isCall = (opcode & 0xfe0e) == 0x940e;
    var isJump = (opcode & 0xfe0e) == 0x940c;
    if (isCall || isJump)
    {
        if (word + 1 >= image.Length / 2)
        {
            target = 0;
            operation = "";
            return false;
        }
        target = GetWord(image, word + 1) |
                 ((opcode & 1) << 16) |
                 ((opcode & 0x1f0) << 13);
        operation = isCall ? "CALL" : "JMP";
        return true;
    }

    var isRelativeCall = (opcode & 0xf000) == 0xd000;
    var isRelativeJump = (opcode & 0xf000) == 0xc000;
    if (isRelativeCall || isRelativeJump)
    {
        target = word + 1 + SignExtend12(opcode & 0x0fff);
        operation = isRelativeCall ? "RCALL" : "RJMP";
        return true;
    }

    target = 0;
    operation = "";
    return false;
}

static int ParseHex(string value) =>
    int.Parse(
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

static int GetWord(byte[] bytes, int word) =>
    bytes[word * 2] | bytes[word * 2 + 1] << 8;

static int SignExtend12(int value) => (value & 0x800) != 0 ? value - 0x1000 : value;

static bool IsTwoWordInstruction(int opcode) =>
    (opcode & 0xfe0f) == 0x9000 ||
    (opcode & 0xfe0f) == 0x9200 ||
    (opcode & 0xfe0e) == 0x940e ||
    (opcode & 0xfe0e) == 0x940c;

readonly record struct ControlFlowReference(int Site, int Target, string Operation);
