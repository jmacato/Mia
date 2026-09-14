// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Rom;

internal static class RomCallOracle
{
    const int FirstEntry = 0x3f0000;
    const int LastEntry = 0x3f0200;

    static readonly int[] SoftwareStackRegisters =
    [
        25, 26, 27, 24,
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    static readonly (int Start, int End)[] ExecutableRanges =
    [
        (0x000000, 0x0000a4),
        (0x00010a, 0x0001aa),
        (0x00212a, 0x004f12),
        (0x020000, 0x420000),
    ];

    public static RomOracleReport Analyze(byte[] firmware)
    {
        var references = new List<RomReference>();
        foreach (var (rangeStart, rangeEnd) in ExecutableRanges)
        {
            AnalyzeRange(firmware, rangeStart, rangeEnd, references);
        }

        return new RomOracleReport { References = references };
    }

    static void AnalyzeRange(
        byte[] firmware,
        int rangeStart,
        int rangeEnd,
        List<RomReference> references)
    {
        int firstWord = rangeStart / 2;
        int lastWord = Math.Min(rangeEnd, firmware.Length) / 2;
        for (int word = firstWord; word + 1 < lastWord; word++)
        {
            int opcode = GetWord(firmware, word);
            RomReference? reference = DecodeReference(
                firmware,
                word,
                firstWord,
                opcode);
            if (reference is not null)
            {
                references.Add(reference);
            }
            if (IsTwoWordInstruction(opcode))
            {
                word++;
            }
        }
    }

    static RomReference? DecodeReference(
        byte[] firmware,
        int word,
        int firstWord,
        int opcode)
    {
        bool isCall = (opcode & 0xfe0e) == 0x940e;
        bool isJump = (opcode & 0xfe0e) == 0x940c;
        if (!isCall && !isJump)
        {
            return null;
        }
        int target = GetLongTarget(firmware, word, opcode);
        if (target is < FirstEntry or > LastEntry)
        {
            return null;
        }
        int? cleanup = FindCleanupBytes(firmware, word, firstWord);
        return new RomReference(target, isCall, word, cleanup);
    }

    static int? FindCleanupBytes(byte[] firmware, int word, int firstWord)
    {
        if (word <= firstWord)
        {
            return null;
        }
        int previous = GetWord(firmware, word - 1);
        return (previous & 0xf0f0) == 0xe0e0 // LDI r30, K
            ? (previous & 0xf) | ((previous & 0xf00) >> 4)
            : null;
    }

    public static void Print(RomOracleReport report, TextWriter output)
    {
        output.WriteLine("Shared software-stack prologue/epilogue family:");
        output.WriteLine("saved ABI registers                  prologue entry/calls  epilogue entry/jumps  observed cleanup bytes");
        for (var saved = 3; saved <= 16; saved++)
        {
            var prologue = 0x3f0000 + saved * 2;
            var epilogue = prologue + 0x22;
            var calls = report.References.Count(reference => reference.Entry == prologue && reference.IsCall);
            var exits = report.References.Count(reference => reference.Entry == epilogue && !reference.IsCall);
            var cleanup = report.References
                .Where(reference => reference.Entry == epilogue && !reference.IsCall && reference.CleanupBytes.HasValue)
                .Select(reference => reference.CleanupBytes!.Value)
                .GroupBy(value => value)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Take(6)
                .Select(group => $"{group.Key}:{group.Count()}");
            var registers = string.Join(',', SoftwareStackRegisters.Take(saved).Select(register => $"r{register}"));
            output.WriteLine($"{registers,-36} 0x{prologue:x6}/{calls,-5}     0x{epilogue:x6}/{exits,-5}      {string.Join(' ', cleanup)}");
        }

        output.WriteLine();
        output.WriteLine("Other high-ROM entries referenced from firmware regions:");
        foreach (var group in report.Entries
                     .Where(group => !IsPrologueOrEpilogue(group.Key.Entry))
                     .OrderBy(group => group.Key.Entry)
                     .ThenByDescending(group => group.Key.IsCall))
        {
            output.WriteLine($"0x{group.Key.Entry:x6} {(group.Key.IsCall ? "CALL" : "JMP ")} sites={group.Count()}");
        }
    }

    static bool IsPrologueOrEpilogue(int entry) =>
        entry is >= 0x3f0006 and <= 0x3f0020 && (entry & 1) == 0 ||
        entry is >= 0x3f0028 and <= 0x3f0042 && (entry & 1) == 0;

    static bool IsTwoWordInstruction(int opcode) =>
        (opcode & 0xfe0f) == 0x9000 || // LDS
        (opcode & 0xfe0f) == 0x9200 || // STS
        (opcode & 0xfe0e) == 0x940e || // CALL
        (opcode & 0xfe0e) == 0x940c;   // JMP

    static int GetLongTarget(byte[] bytes, int word, int opcode) =>
        GetWord(bytes, word + 1) | ((opcode & 1) << 16) | ((opcode & 0x1f0) << 13);

    static int GetWord(byte[] bytes, int word) => bytes[word * 2] | (bytes[word * 2 + 1] << 8);
}
