// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    // Order is semantic: overlapping masks use the first matching definition.
    static readonly AvrInstructionDefinition[] SlowInstructions =
    [
        new(0xfc00, 0x1c00, ExecuteAdc), // ADC
        new(0xfc00, 0xc00, ExecuteAdd), // ADD
        new(0xff00, 0x9600, ExecuteAdiw), // ADIW
        new(0xfc00, 0x2000, ExecuteAnd), // AND
        new(0xf000, 0x7000, ExecuteAndi), // ANDI
        new(0xfe0f, 0x9405, ExecuteAsr), // ASR
        new(0xff8f, 0x9488, ExecuteBclr), // BCLR
        new(0xfe08, 0xf800, ExecuteBld), // BLD
        new(0xfc00, 0xf400, ExecuteBrbc), // BRBC
        new(0xfc00, 0xf000, ExecuteBrbs), // BRBS
        new(0xff8f, 0x9408, ExecuteBset), // BSET
        new(0xfe08, 0xfa00, ExecuteBst), // BST
        new(0xfe0e, 0x940e, ExecuteCall), // CALL
        new(0xff00, 0x9800, ExecuteCbi), // CBI
        new(0xfe0f, 0x9400, ExecuteCom), // COM
        new(0xfc00, 0x1400, ExecuteCp), // CP
        new(0xfc00, 0x400, ExecuteCpc), // CPC
        new(0xf000, 0x3000, ExecuteCpi), // CPI
        new(0xfc00, 0x1000, ExecuteCpse), // CPSE
        new(0xfe0f, 0x940a, ExecuteDec), // DEC
        new(0xffff, 0x9519, ExecuteEicall), // EICALL
        new(0xffff, 0x9419, ExecuteEijmp), // EIJMP
        new(0xffff, 0x95d8, ExecuteElpm), // ELPM
        new(0xfe0f, 0x9006, ExecuteElpmRegister), // ELPM(REG)
        new(0xfe0f, 0x9007, ExecuteElpmPostIncrement), // ELPM(INC)
        new(0xfc00, 0x2400, ExecuteEor), // EOR
        new(0xff88, 0x308, ExecuteFmul), // FMUL
        new(0xff88, 0x380, ExecuteFmuls), // FMULS
        new(0xff88, 0x388, ExecuteFmulsu), // FMULSU
        new(0xffff, 0x9509, ExecuteIcall), // ICALL
        new(0xffff, 0x9409, ExecuteIjmp), // IJMP
        new(0xf800, 0xb000, ExecuteIn), // IN
        new(0xfe0f, 0x9403, ExecuteInc), // INC
        new(0xfe0e, 0x940c, ExecuteJmp), // JMP
        new(0xfe0f, 0x9206, ExecuteLac), // LAC
        new(0xfe0f, 0x9205, ExecuteLas), // LAS
        new(0xfe0f, 0x9207, ExecuteLat), // LAT
        new(0xf000, 0xe000, ExecuteLdi), // LDI
        new(0xfe0f, 0x9000, ExecuteLds), // LDS
        new(0xfe0f, 0x900c, ExecuteLdx), // LDX
        new(0xfe0f, 0x900d, ExecuteLdxPostIncrement), // LDX(INC)
        new(0xfe0f, 0x900e, ExecuteLdxPreDecrement), // LDX(DEC)
        new(0xfe0f, 0x8008, ExecuteLdy), // LDY
        new(0xfe0f, 0x9009, ExecuteLdyPostIncrement), // LDY(INC)
        new(0xfe0f, 0x900a, ExecuteLdyPreDecrement), // LDY(DEC)
        new(0xd208, 0x8008, ExecuteLddy, requiresNonzeroDisplacement: true), // LDDY
        new(0xfe0f, 0x8000, ExecuteLdz), // LDZ
        new(0xfe0f, 0x9001, ExecuteLdzPostIncrement), // LDZ(INC)
        new(0xfe0f, 0x9002, ExecuteLdzPreDecrement), // LDZ(DEC)
        new(0xd208, 0x8000, ExecuteLddz, requiresNonzeroDisplacement: true), // LDDZ
        new(0xffff, 0x95c8, ExecuteLpm), // LPM
        new(0xfe0f, 0x9004, ExecuteLpmRegister), // LPM(REG)
        new(0xfe0f, 0x9005, ExecuteLpmPostIncrement), // LPM(INC)
        new(0xfe0f, 0x9406, ExecuteLsr), // LSR
        new(0xfc00, 0x2c00, ExecuteMov), // MOV
        new(0xff00, 0x100, ExecuteMovw), // MOVW
        new(0xfc00, 0x9c00, ExecuteMul), // MUL
        new(0xff00, 0x200, ExecuteMuls), // MULS
        new(0xff88, 0x300, ExecuteMulsu), // MULSU
        new(0xfe0f, 0x9401, ExecuteNeg), // NEG
        new(0xffff, 0, ExecuteNop), // NOP
        new(0xfc00, 0x2800, ExecuteOr), // OR
        new(0xf000, 0x6000, ExecuteSbr), // SBR
        new(0xf800, 0xb800, ExecuteOut), // OUT
        new(0xfe0f, 0x900f, ExecutePop), // POP
        new(0xfe0f, 0x920f, ExecutePush), // PUSH
        new(0xf000, 0xd000, ExecuteRcall), // RCALL
        new(0xffff, 0x9508, ExecuteRet), // RET
        new(0xffff, 0x9518, ExecuteReti), // RETI
        new(0xf000, 0xc000, ExecuteRjmp), // RJMP
        new(0xfe0f, 0x9407, ExecuteRor), // ROR
        new(0xfc00, 0x800, ExecuteSbc), // SBC
        new(0xf000, 0x4000, ExecuteSbci), // SBCI
        new(0xff00, 0x9a00, ExecuteSbi), // SBI
        new(0xff00, 0x9900, ExecuteSbic), // SBIC
        new(0xff00, 0x9b00, ExecuteSbis), // SBIS
        new(0xff00, 0x9700, ExecuteSbiw), // SBIW
        new(0xfe08, 0xfc00, ExecuteSbrc), // SBRC
        new(0xfe08, 0xfe00, ExecuteSbrs), // SBRS
        new(0xffff, 0x9588, ExecuteSleep), // SLEEP
        new(0xffff, 0x95e8, ExecuteSpm), // SPM
        new(0xffff, 0x95f8, ExecuteSpmPostIncrement), // SPM(INC)
        new(0xfe0f, 0x9200, ExecuteSts), // STS
        new(0xfe0f, 0x920c, ExecuteStx), // STX
        new(0xfe0f, 0x920d, ExecuteStxPostIncrement), // STX(INC)
        new(0xfe0f, 0x920e, ExecuteStxPreDecrement), // STX(DEC)
        new(0xfe0f, 0x8208, ExecuteSty), // STY
        new(0xfe0f, 0x9209, ExecuteStyPostIncrement), // STY(INC)
        new(0xfe0f, 0x920a, ExecuteStyPreDecrement), // STY(DEC)
        new(0xd208, 0x8208, ExecuteStdy, requiresNonzeroDisplacement: true), // STDY
        new(0xfe0f, 0x8200, ExecuteStz), // STZ
        new(0xfe0f, 0x9201, ExecuteStzPostIncrement), // STZ(INC)
        new(0xfe0f, 0x9202, ExecuteStzPreDecrement), // STZ(DEC)
        new(0xd208, 0x8200, ExecuteStdz, requiresNonzeroDisplacement: true), // STDZ
        new(0xfc00, 0x1800, ExecuteSub), // SUB
        new(0xf000, 0x5000, ExecuteSubi), // SUBI
        new(0xfe0f, 0x9402, ExecuteSwap), // SWAP
        new(0xffff, 0x95a8, ExecuteWdr), // WDR
        new(0xfe0f, 0x9204, ExecuteXch), // XCH
    ];

    static readonly byte[] InstructionIndices = CreateInstructionIndices();

    static byte[] CreateInstructionIndices()
    {
        var indices = new byte[ushort.MaxValue + 1];
        Array.Fill(indices, byte.MaxValue);
        for (var opcode = 0; opcode < indices.Length; opcode++)
        {
            for (var index = 0; index < SlowInstructions.Length; index++)
            {
                if (SlowInstructions[index].Matches(opcode))
                {
                    indices[opcode] = checked((byte)index);
                    break;
                }
            }
        }
        return indices;
    }
}
