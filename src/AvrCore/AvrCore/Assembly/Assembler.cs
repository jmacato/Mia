// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors; Michael Conrad Tadpol Tilstra (ported from avr8js assembler.ts / Avrian-Jump)

using System.Globalization;
using System.Text.RegularExpressions;

namespace AvrCore.Assembly;

internal static class Assembler
{
    static readonly Regex RegisterPattern = new(@"[Rr](\d{1,2})", RegexOptions.Compiled);
    static readonly Regex PairRegisterPattern = new(@"[Rr](24|26|28|30)", RegexOptions.Compiled);
    static readonly Regex DisplacementPattern = new(@"^([YZ])\+(\d+)$", RegexOptions.Compiled);
    internal static readonly Regex CommentPattern = new(@"[#;].*$", RegexOptions.Compiled);
    internal static readonly Regex LabelPattern = new(@"^(\w+):", RegexOptions.Compiled);
    internal static readonly Regex CodePattern = new(@"^\s*(\w+)(?:\s+([^,]+)(?:,\s*(\S+))?)?\s*$", RegexOptions.Compiled);

    internal static bool TryParseNumber(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        text = text.Trim();
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        bool parsedSuccessfully = TryParseMagnitude(text, out int parsed);
        value = negative ? -parsed : parsed;
        return parsedSuccessfully;
    }

    static bool TryParseMagnitude(string text, out int value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(
                text[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value)
            : int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);

    /// <summary>Destination register index, shifted to bits 4-8. Validates the allowed range.</summary>
    static int DestRegister(string? operand, int min = 0, int max = 31)
    {
        var match = RegisterPattern.Match(operand ?? "");
        if (!match.Success)
        {
            throw new AssemblerException("Not a register: " + operand);
        }
        var d = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (d < min || d > max)
        {
            throw new AssemblerException($"Rd out of range: {min}<>{max}");
        }
        return (d & 0x1f) << 4;
    }

    /// <summary>Source register index, split across bits 0-3 and 9. Validates the allowed range.</summary>
    static int SrcRegister(string? operand, int min = 0, int max = 31)
    {
        var match = RegisterPattern.Match(operand ?? "");
        if (!match.Success)
        {
            throw new AssemblerException("Not a register: " + operand);
        }
        var d = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (d < min || d > max)
        {
            throw new AssemblerException($"Rd out of range: {min}<>{max}");
        }
        return (d & 0xf) | (((d >> 4) & 1) << 9);
    }

    static int ConstValue(string? operand, int min = 0, int max = 255)
    {
        if (!TryParseNumber(operand, out var value))
        {
            throw new AssemblerException("constant is not a number.");
        }
        return ConstValue(value, min, max);
    }

    static int ConstValue(int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new AssemblerException($"[Ks] out of range: {min}<>{max}");
        }
        return value;
    }

    /// <summary>Encode a signed value as two's complement in the given number of bits.</summary>
    static int FitTwoC(int value, int bits)
    {
        if (Math.Abs(value) > (1 << (bits - 1)))
        {
            throw new AssemblerException($"Not enough bits for number. ({value}, {bits})");
        }
        return value & ((1 << bits) - 1);
    }

    /// <summary>Resolve a numeric constant or label reference. Returns null when the label is not (yet) known.</summary>
    static int? ResolveValue(string? operand, Dictionary<string, int> labels, int relativeTo = 0)
    {
        if (TryParseNumber(operand, out var value))
        {
            return value;
        }
        if (operand != null && labels.TryGetValue(operand, out var address))
        {
            return address - relativeTo;
        }
        return null;
    }

    /// <summary>Indirect address register code for LD/ST (X, X+, -X, Y, ..., -Z).</summary>
    static int PointerCode(string? operand) => operand switch
    {
        "X" => 0x900c,
        "X+" => 0x900d,
        "-X" => 0x900e,
        "Y" => 0x8008,
        "Y+" => 0x9009,
        "-Y" => 0x900a,
        "Z" => 0x8000,
        "Z+" => 0x9001,
        "-Z" => 0x9002,
        _ => throw new AssemblerException("Invalid pointer register: " + operand),
    };

    /// <summary>Indirect address register with displacement (Y+q / Z+q) for LDD/STD.</summary>
    static int DisplacedPointerCode(string? operand)
    {
        var match = DisplacementPattern.Match(operand ?? "");
        if (!match.Success)
        {
            throw new AssemblerException("Invalid arguments");
        }
        var r = 0x8000 | (match.Groups[1].Value == "Y" ? 0x8 : 0);
        var q = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (q < 0 || q > 64)
        {
            throw new AssemblerException("q is out of range");
        }
        return r | ((q & 0x20) << 8) | ((q & 0x18) << 7) | (q & 0x7);
    }

    static int TwoReg(int baseOpcode, string? a, string? b) =>
        baseOpcode | DestRegister(a) | SrcRegister(b);

    static int OneReg(int baseOpcode, string? a) =>
        baseOpcode | DestRegister(a);

    static int Immediate(int baseOpcode, string? rd, int k) =>
        baseOpcode | (DestRegister(rd, 16, 31) & 0xf0) | ((k & 0xf0) << 4) | (k & 0xf);

    static int Immediate(int baseOpcode, string? rd, string? k) =>
        Immediate(baseOpcode, rd, ConstValue(k));

    /// <summary>ADIW/SBIW: register pair r24/r26/r28/r30 with a 6-bit constant.</summary>
    static int WordImmediate(int baseOpcode, string? a, string? b)
    {
        var match = PairRegisterPattern.Match(a ?? "");
        if (!match.Success)
        {
            throw new AssemblerException("Rd must be 24, 26, 28, or 30");
        }
        var d = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 24) / 2;
        var k = ConstValue(b, 0, 63);
        return baseOpcode | ((d & 0x3) << 4) | ((k & 0x30) << 2) | (k & 0x0f);
    }

    /// <summary>CBI/SBI/SBIC/SBIS: I/O address 0-31 with bit number 0-7.</summary>
    static int IoBit(int baseOpcode, string? a, string? b) =>
        baseOpcode | (ConstValue(a, 0, 31) << 3) | ConstValue(b, 0, 7);

    /// <summary>BSET/BCLR and their SEx/CLx aliases.</summary>
    static int StatusFlag(int baseOpcode, int flag) =>
        baseOpcode | ((flag & 0x7) << 4);

    /// <summary>Conditional branches (BRBS/BRBC and aliases). Defers when the target label is not yet known.</summary>
    static OpResult Branch(
        BranchEncoding encoding,
        string? target,
        int byteOffset,
        Dictionary<string, int> labels)
    {
        var k = ResolveValue(target, labels, byteOffset + 2);
        if (k == null)
        {
            return OpResult.Defer(
                1,
                resolved => Branch(encoding, target, byteOffset, resolved));
        }
        var r = encoding.Opcode | ConstValue(encoding.FlagBit, 0, 7);
        return r | (FitTwoC(ConstValue(k.Value >> 1, -64, 63), 7) << 3);
    }

    /// <summary>CALL/JMP: 22-bit absolute target, two words. Defers when the target label is not yet known.</summary>
    static OpResult LongJump(int baseOpcode, string? target, int byteOffset, Dictionary<string, int> labels)
    {
        var k = ResolveValue(target, labels);
        if (k == null)
        {
            return OpResult.Defer(2, resolved => LongJump(baseOpcode, target, byteOffset, resolved));
        }
        var addr = ConstValue(k.Value, 0, 0x7ffffe) >> 1;
        var hk = (addr >> 16) & 0x3f;
        return OpResult.FromWords(
            (ushort)(baseOpcode | ((hk & 0x3e) << 3) | (hk & 1)),
            (ushort)(addr & 0xffff));
    }

    /// <summary>RCALL/RJMP: 12-bit relative target. Defers when the target label is not yet known.</summary>
    static OpResult RelativeJump(int baseOpcode, string? target, int byteOffset, Dictionary<string, int> labels)
    {
        var k = ResolveValue(target, labels, byteOffset + 2);
        if (k == null)
        {
            return OpResult.Defer(1, resolved => RelativeJump(baseOpcode, target, byteOffset, resolved));
        }
        return baseOpcode | FitTwoC(ConstValue(k.Value >> 1, -2048, 2047), 12);
    }

    /// <summary>LPM/ELPM: either implied r0 (no operands) or Rd with Z / Z+.</summary>
    static int LoadProgramMemory(
        ProgramMemoryEncoding encoding,
        string? destination,
        string? pointer)
    {
        if (string.IsNullOrEmpty(destination))
        {
            return encoding.NoOperandOpcode;
        }
        var r = 0x9000 | DestRegister(destination);
        return pointer switch
        {
            "Z" => r | encoding.ZCode,
            "Z+" => r | encoding.ZPlusCode,
            _ => throw new AssemblerException("Bad operand"),
        };
    }

    /// <summary>LAC/LAS/LAT/XCH: first operand must be Z, second is the register.</summary>
    static int ZExchange(int baseOpcode, string? a, string? b)
    {
        if (a != "Z")
        {
            throw new AssemblerException("First Operand is not Z");
        }
        return baseOpcode | DestRegister(b);
    }

    internal static readonly Dictionary<string, AssemblerOpHandler> InstructionHandlers = new()
    {
        ["ADD"] = (a, b, _, _) => TwoReg(0x0c00, a, b),
        ["ADC"] = (a, b, _, _) => TwoReg(0x1c00, a, b),
        ["ADIW"] = (a, b, _, _) => WordImmediate(0x9600, a, b),
        ["AND"] = (a, b, _, _) => TwoReg(0x2000, a, b),
        ["ANDI"] = (a, b, _, _) => Immediate(0x7000, a, b),
        ["ASR"] = (a, _, _, _) => OneReg(0x9405, a),
        ["BCLR"] = (a, _, _, _) => StatusFlag(0x9488, ConstValue(a, 0, 7)),
        ["BLD"] = (a, b, _, _) => 0xf800 | DestRegister(a) | (ConstValue(b, 0, 7) & 0x7),
        ["BRBC"] = (a, b, off, l) => Branch(new(0xf400, a), b, off, l),
        ["BRBS"] = (a, b, off, l) => Branch(new(0xf000, a), b, off, l),
        ["BRCC"] = (a, _, off, l) => Branch(new(0xf400, "0"), a, off, l),
        ["BRCS"] = (a, _, off, l) => Branch(new(0xf000, "0"), a, off, l),
        ["BREAK"] = (_, _, _, _) => 0x9598,
        ["BREQ"] = (a, _, off, l) => Branch(new(0xf000, "1"), a, off, l),
        ["BRGE"] = (a, _, off, l) => Branch(new(0xf400, "4"), a, off, l),
        ["BRHC"] = (a, _, off, l) => Branch(new(0xf400, "5"), a, off, l),
        ["BRHS"] = (a, _, off, l) => Branch(new(0xf000, "5"), a, off, l),
        ["BRID"] = (a, _, off, l) => Branch(new(0xf400, "7"), a, off, l),
        ["BRIE"] = (a, _, off, l) => Branch(new(0xf000, "7"), a, off, l),
        ["BRLO"] = (a, _, off, l) => Branch(new(0xf000, "0"), a, off, l),
        ["BRLT"] = (a, _, off, l) => Branch(new(0xf000, "4"), a, off, l),
        ["BRMI"] = (a, _, off, l) => Branch(new(0xf000, "2"), a, off, l),
        ["BRNE"] = (a, _, off, l) => Branch(new(0xf400, "1"), a, off, l),
        ["BRPL"] = (a, _, off, l) => Branch(new(0xf400, "2"), a, off, l),
        ["BRSH"] = (a, _, off, l) => Branch(new(0xf400, "0"), a, off, l),
        ["BRTC"] = (a, _, off, l) => Branch(new(0xf400, "6"), a, off, l),
        ["BRTS"] = (a, _, off, l) => Branch(new(0xf000, "6"), a, off, l),
        ["BRVC"] = (a, _, off, l) => Branch(new(0xf400, "3"), a, off, l),
        ["BRVS"] = (a, _, off, l) => Branch(new(0xf000, "3"), a, off, l),
        ["BSET"] = (a, _, _, _) => StatusFlag(0x9408, ConstValue(a, 0, 7)),
        ["BST"] = (a, b, _, _) => 0xfa00 | DestRegister(a) | ConstValue(b, 0, 7),
        ["CALL"] = (a, _, off, l) => LongJump(0x940e, a, off, l),
        ["CBI"] = (a, b, _, _) => IoBit(0x9800, a, b),
        ["CBR"] = (a, b, _, _) => Immediate(0x7000, a, ~ConstValue(b) & 0xff),
        ["CLC"] = (_, _, _, _) => StatusFlag(0x9488, 0),
        ["CLH"] = (_, _, _, _) => StatusFlag(0x9488, 5),
        ["CLI"] = (_, _, _, _) => StatusFlag(0x9488, 7),
        ["CLN"] = (_, _, _, _) => StatusFlag(0x9488, 2),
        ["CLR"] = (a, _, _, _) => TwoReg(0x2400, a, a),
        ["CLS"] = (_, _, _, _) => StatusFlag(0x9488, 4),
        ["CLT"] = (_, _, _, _) => StatusFlag(0x9488, 6),
        ["CLV"] = (_, _, _, _) => StatusFlag(0x9488, 3),
        ["CLZ"] = (_, _, _, _) => StatusFlag(0x9488, 1),
        ["COM"] = (a, _, _, _) => OneReg(0x9400, a),
        ["CP"] = (a, b, _, _) => TwoReg(0x1400, a, b),
        ["CPC"] = (a, b, _, _) => TwoReg(0x0400, a, b),
        ["CPI"] = (a, b, _, _) => Immediate(0x3000, a, b),
        ["CPSE"] = (a, b, _, _) => TwoReg(0x1000, a, b),
        ["DEC"] = (a, _, _, _) => OneReg(0x940a, a),
        ["DES"] = (a, _, _, _) => 0x940b | (ConstValue(a, 0, 15) << 4),
        ["EICALL"] = (_, _, _, _) => 0x9519,
        ["EIJMP"] = (_, _, _, _) => 0x9419,
        ["ELPM"] = (a, b, _, _) => LoadProgramMemory(new(0x95d8, 6, 7), a, b),
        ["EOR"] = (a, b, _, _) => TwoReg(0x2400, a, b),
        ["FMUL"] = (a, b, _, _) => 0x0308 | (DestRegister(a, 16, 23) & 0x70) | (SrcRegister(b, 16, 23) & 0x7),
        ["FMULS"] = (a, b, _, _) => 0x0380 | (DestRegister(a, 16, 23) & 0x70) | (SrcRegister(b, 16, 23) & 0x7),
        ["FMULSU"] = (a, b, _, _) => 0x0388 | (DestRegister(a, 16, 23) & 0x70) | (SrcRegister(b, 16, 23) & 0x7),
        ["ICALL"] = (_, _, _, _) => 0x9509,
        ["IJMP"] = (_, _, _, _) => 0x9409,
        ["IN"] = (a, b, _, _) =>
        {
            var addr = ConstValue(b, 0, 63);
            return 0xb000 | DestRegister(a) | ((addr & 0x30) << 5) | (addr & 0x0f);
        },
        ["INC"] = (a, _, _, _) => OneReg(0x9403, a),
        ["JMP"] = (a, _, off, l) => LongJump(0x940c, a, off, l),
        ["LAC"] = (a, b, _, _) => ZExchange(0x9206, a, b),
        ["LAS"] = (a, b, _, _) => ZExchange(0x9205, a, b),
        ["LAT"] = (a, b, _, _) => ZExchange(0x9207, a, b),
        ["LD"] = (a, b, _, _) => DestRegister(a) | PointerCode(b),
        ["LDD"] = (a, b, _, _) => DestRegister(a) | DisplacedPointerCode(b),
        ["LDI"] = (a, b, _, _) => Immediate(0xe000, a, b),
        ["LDS"] = (a, b, _, _) => OpResult.FromWords(
            (ushort)OneReg(0x9000, a),
            (ushort)ConstValue(b, 0, 65535)),
        ["LPM"] = (a, b, _, _) => LoadProgramMemory(new(0x95c8, 4, 5), a, b),
        ["LSL"] = (a, _, _, _) => TwoReg(0x0c00, a, a),
        ["LSR"] = (a, _, _, _) => OneReg(0x9406, a),
        ["MOV"] = (a, b, _, _) => TwoReg(0x2c00, a, b),
        ["MOVW"] = (a, b, _, _) => 0x0100 | ((DestRegister(a) >> 1) & 0xf0) | ((DestRegister(b) >> 5) & 0xf),
        ["MUL"] = (a, b, _, _) => TwoReg(0x9c00, a, b),
        ["MULS"] = (a, b, _, _) => 0x0200 | (DestRegister(a, 16, 31) & 0xf0) | (SrcRegister(b, 16, 31) & 0xf),
        ["MULSU"] = (a, b, _, _) => 0x0300 | (DestRegister(a, 16, 23) & 0x70) | (SrcRegister(b, 16, 23) & 0x7),
        ["NEG"] = (a, _, _, _) => OneReg(0x9401, a),
        ["NOP"] = (_, _, _, _) => 0x0000,
        ["OR"] = (a, b, _, _) => TwoReg(0x2800, a, b),
        ["ORI"] = (a, b, _, _) => Immediate(0x6000, a, b),
        ["OUT"] = (a, b, _, _) =>
        {
            var addr = ConstValue(a, 0, 63);
            return 0xb800 | DestRegister(b) | ((addr & 0x30) << 5) | (addr & 0x0f);
        },
        ["POP"] = (a, _, _, _) => OneReg(0x900f, a),
        ["PUSH"] = (a, _, _, _) => OneReg(0x920f, a),
        ["RCALL"] = (a, _, off, l) => RelativeJump(0xd000, a, off, l),
        ["RET"] = (_, _, _, _) => 0x9508,
        ["RETI"] = (_, _, _, _) => 0x9518,
        ["RJMP"] = (a, _, off, l) => RelativeJump(0xc000, a, off, l),
        ["ROL"] = (a, _, _, _) => TwoReg(0x1c00, a, a),
        ["ROR"] = (a, _, _, _) => OneReg(0x9407, a),
        ["SBC"] = (a, b, _, _) => TwoReg(0x0800, a, b),
        ["SBCI"] = (a, b, _, _) => Immediate(0x4000, a, b),
        ["SBI"] = (a, b, _, _) => IoBit(0x9a00, a, b),
        ["SBIC"] = (a, b, _, _) => IoBit(0x9900, a, b),
        ["SBIS"] = (a, b, _, _) => IoBit(0x9b00, a, b),
        ["SBIW"] = (a, b, _, _) => WordImmediate(0x9700, a, b),
        ["SBR"] = (a, b, _, _) => Immediate(0x6000, a, b),
        ["SBRC"] = (a, b, _, _) => 0xfc00 | DestRegister(a) | ConstValue(b, 0, 7),
        ["SBRS"] = (a, b, _, _) => 0xfe00 | DestRegister(a) | ConstValue(b, 0, 7),
        ["SEC"] = (_, _, _, _) => StatusFlag(0x9408, 0),
        ["SEH"] = (_, _, _, _) => StatusFlag(0x9408, 5),
        ["SEI"] = (_, _, _, _) => StatusFlag(0x9408, 7),
        ["SEN"] = (_, _, _, _) => StatusFlag(0x9408, 2),
        ["SER"] = (a, _, _, _) => 0xef0f | (DestRegister(a, 16, 31) & 0xf0),
        ["SES"] = (_, _, _, _) => StatusFlag(0x9408, 4),
        ["SET"] = (_, _, _, _) => StatusFlag(0x9408, 6),
        ["SEV"] = (_, _, _, _) => StatusFlag(0x9408, 3),
        ["SEZ"] = (_, _, _, _) => StatusFlag(0x9408, 1),
        ["SLEEP"] = (_, _, _, _) => 0x9588,
        ["SPM"] = (a, _, _, _) => string.IsNullOrEmpty(a)
            ? 0x95e8
            : a == "Z+" ? 0x95f8 : throw new AssemblerException("Bad param to SPM"),
        ["ST"] = (a, b, _, _) => 0x0200 | DestRegister(b) | PointerCode(a),
        ["STD"] = (a, b, _, _) => 0x0200 | DestRegister(b) | DisplacedPointerCode(a),
        ["STS"] = (a, b, _, _) => OpResult.FromWords(
            (ushort)OneReg(0x9200, b),
            (ushort)ConstValue(a, 0, 65535)),
        ["SUB"] = (a, b, _, _) => TwoReg(0x1800, a, b),
        ["SUBI"] = (a, b, _, _) => Immediate(0x5000, a, b),
        ["SWAP"] = (a, _, _, _) => OneReg(0x9402, a),
        ["TST"] = (a, _, _, _) => TwoReg(0x2000, a, a),
        ["WDR"] = (_, _, _, _) => 0x95a8,
        ["XCH"] = (a, b, _, _) => ZExchange(0x9204, a, b),
    };

    internal static string? ApplyReplacement(
        string? operand,
        Dictionary<string, string?> replacements) =>
        operand != null && replacements.TryGetValue(operand, out var replacement)
            ? replacement
            : operand;

    public static AssembleResult Assemble(string source) =>
        new AssemblerPass(source).Assemble();

    internal static void ResolveAndEmit(AssembleResult result)
    {
        var lastLine = result.Lines.Count > 0 ? result.Lines[^1] : null;
        var byteSize = lastLine != null ? lastLine.ByteOffset + lastLine.SizeWords * 2 : 0;
        var output = new byte[byteSize];

        foreach (var line in result.Lines)
        {
            try
            {
                ResolveLine(result, line);
                WriteLine(output, line);
            }
            catch (AssemblerException error)
            {
                result.Errors.Add($"Line {line.Line}: {error.Message}");
            }
        }

        result.Bytes = result.Errors.Count == 0 ? output : Array.Empty<byte>();
    }

    static void ResolveLine(AssembleResult result, AssemblerLine line)
    {
        if (line.Deferred is not null)
        {
            OpResult resolved = line.Deferred(result.Labels);
            line.Words = resolved.Words ??
                throw new AssemblerException("Unresolved label reference.");
            line.Deferred = null;
        }
        if (line.Words is null || line.Words.Count == 0)
        {
            throw new AssemblerException("Empty code line.");
        }
    }

    static void WriteLine(byte[] output, AssemblerLine line)
    {
        IReadOnlyList<ushort> words = line.Words!;
        for (int index = 0, offset = line.ByteOffset;
            index < words.Count;
            index++, offset += 2)
        {
            output[offset] = (byte)words[index];
            output[offset + 1] = (byte)(words[index] >> 8);
        }
    }
}
