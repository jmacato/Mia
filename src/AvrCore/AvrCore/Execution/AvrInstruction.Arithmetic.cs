// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    static void ExecuteAdc(Cpu cpu, int opcode)
    {
        /* ADC, 0001 11rd dddd rrrr */
        int d = cpu.Data[(opcode & 0x1f0) >> 4];
        int r = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        var sum = d + r + (cpu.Data[95] & 1);
        var R = sum & 255;
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= ((R ^ r) & (d ^ R) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (sum & 256) != 0 ? 1 : 0;
        sreg |= (1 & ((d & r) | (r & ~R) | (~R & d))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteAdd(Cpu cpu, int opcode)
    {
        /* ADD, 0000 11rd dddd rrrr */
        int d = cpu.Data[(opcode & 0x1f0) >> 4];
        int r = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        var R = (d + r) & 255;
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= ((R ^ r) & (R ^ d) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= ((d + r) & 256) != 0 ? 1 : 0;
        sreg |= (1 & ((d & r) | (r & ~R) | (~R & d))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteAdiw(Cpu cpu, int opcode)
    {
        /* ADIW, 1001 0110 KKdd KKKK */
        var addr = 2 * ((opcode & 0x30) >> 4) + 24;
        var value = cpu.GetUint16(addr);
        var R = (value + ((opcode & 0xf) | ((opcode & 0xc0) >> 2))) & 0xffff;
        cpu.SetUint16(addr, R);
        var sreg = cpu.Data[95] & 0xe0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (0x8000 & R) != 0 ? 4 : 0;
        sreg |= (~value & R & 0x8000) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (~R & value & 0x8000) != 0 ? 1 : 0;
        cpu.Data[95] = (byte)sreg;
        cpu.Cycles++;
    }

    static void ExecuteAnd(Cpu cpu, int opcode)
    {
        /* AND, 0010 00rd dddd rrrr */
        var R = cpu.Data[(opcode & 0x1f0) >> 4] & cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteAndi(Cpu cpu, int opcode)
    {
        /* ANDI, 0111 KKKK dddd KKKK */
        var R = cpu.Data[((opcode & 0xf0) >> 4) + 16] & ((opcode & 0xf) | ((opcode & 0xf00) >> 4));
        cpu.Data[((opcode & 0xf0) >> 4) + 16] = (byte)R;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteAsr(Cpu cpu, int opcode)
    {
        /* ASR, 1001 010d dddd 0101 */
        int value = cpu.Data[(opcode & 0x1f0) >> 4];
        var R = (value >> 1) | (128 & value);
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xe0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= value & 1;
        sreg |= (((sreg >> 2) & 1) ^ (sreg & 1)) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteCom(Cpu cpu, int opcode)
    {
        /* COM, 1001 010d dddd 0000 */
        var d = (opcode & 0x1f0) >> 4;
        var R = 255 - cpu.Data[d];
        cpu.Data[d] = (byte)R;
        var sreg = (cpu.Data[95] & 0xe1) | 1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteCp(Cpu cpu, int opcode)
    {
        /* CP, 0001 01rd dddd rrrr */
        int val1 = cpu.Data[(opcode & 0x1f0) >> 4];
        int val2 = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        var R = val1 - val2;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= 0 != ((val1 ^ val2) & (val1 ^ R) & 128) ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= val2 > val1 ? 1 : 0;
        sreg |= (1 & ((~val1 & val2) | (val2 & R) | (R & ~val1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteCpc(Cpu cpu, int opcode)
    {
        /* CPC, 0000 01rd dddd rrrr */
        int arg1 = cpu.Data[(opcode & 0x1f0) >> 4];
        int arg2 = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int sreg = cpu.Data[95];
        var r = arg1 - arg2 - (sreg & 1);
        sreg = (sreg & 0xc0) | (r == 0 && ((sreg >> 1) & 1) != 0 ? 2 : 0) | (arg2 + (sreg & 1) > arg1 ? 1 : 0);
        sreg |= (128 & r) != 0 ? 4 : 0;
        sreg |= ((arg1 ^ arg2) & (arg1 ^ r) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (1 & ((~arg1 & arg2) | (arg2 & r) | (r & ~arg1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteCpi(Cpu cpu, int opcode)
    {
        /* CPI, 0011 KKKK dddd KKKK */
        int arg1 = cpu.Data[((opcode & 0xf0) >> 4) + 16];
        var arg2 = (opcode & 0xf) | ((opcode & 0xf00) >> 4);
        var r = arg1 - arg2;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= r != 0 ? 0 : 2;
        sreg |= (128 & r) != 0 ? 4 : 0;
        sreg |= ((arg1 ^ arg2) & (arg1 ^ r) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= arg2 > arg1 ? 1 : 0;
        sreg |= (1 & ((~arg1 & arg2) | (arg2 & r) | (r & ~arg1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteDec(Cpu cpu, int opcode)
    {
        /* DEC, 1001 010d dddd 1010 */
        int value = cpu.Data[(opcode & 0x1f0) >> 4];
        var R = value - 1;
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= 128 == value ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteEor(Cpu cpu, int opcode)
    {
        /* EOR, 0010 01rd dddd rrrr */
        var R = cpu.Data[(opcode & 0x1f0) >> 4] ^ cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteInc(Cpu cpu, int opcode)
    {
        /* INC, 1001 010d dddd 0011 */
        int d = cpu.Data[(opcode & 0x1f0) >> 4];
        var r = (d + 1) & 255;
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)r;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= r != 0 ? 0 : 2;
        sreg |= (128 & r) != 0 ? 4 : 0;
        sreg |= 127 == d ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteLsr(Cpu cpu, int opcode)
    {
        /* LSR, 1001 010d dddd 0110 */
        int value = cpu.Data[(opcode & 0x1f0) >> 4];
        var R = value >> 1;
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xe0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= value & 1;
        sreg |= (((sreg >> 2) & 1) ^ (sreg & 1)) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteNeg(Cpu cpu, int opcode)
    {
        /* NEG, 1001 010d dddd 0001 */
        var d = (opcode & 0x1f0) >> 4;
        int value = cpu.Data[d];
        var R = 0 - value;
        cpu.Data[d] = (byte)R;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (255 & R) == 128 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= R != 0 ? 1 : 0;
        sreg |= (8 & (R | value)) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteOr(Cpu cpu, int opcode)
    {
        /* OR, 0010 10rd dddd rrrr */
        var R = cpu.Data[(opcode & 0x1f0) >> 4] | cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteSbr(Cpu cpu, int opcode)
    {
        /* SBR, 0110 KKKK dddd KKKK */
        var R = cpu.Data[((opcode & 0xf0) >> 4) + 16] | ((opcode & 0xf) | ((opcode & 0xf00) >> 4));
        cpu.Data[((opcode & 0xf0) >> 4) + 16] = (byte)R;
        var sreg = cpu.Data[95] & 0xe1;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteRor(Cpu cpu, int opcode)
    {
        /* ROR, 1001 010d dddd 0111 */
        int d = cpu.Data[(opcode & 0x1f0) >> 4];
        var r = (d >> 1) | ((cpu.Data[95] & 1) << 7);
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)r;
        var sreg = cpu.Data[95] & 0xe0;
        sreg |= r != 0 ? 0 : 2;
        sreg |= (128 & r) != 0 ? 4 : 0;
        sreg |= (1 & d) != 0 ? 1 : 0;
        sreg |= (((sreg >> 2) & 1) ^ (sreg & 1)) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteSbc(Cpu cpu, int opcode)
    {
        /* SBC, 0000 10rd dddd rrrr */
        int val1 = cpu.Data[(opcode & 0x1f0) >> 4];
        int val2 = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int sreg = cpu.Data[95];
        var R = val1 - val2 - (sreg & 1);
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        sreg = (sreg & 0xc0) | (R == 0 && ((sreg >> 1) & 1) != 0 ? 2 : 0) | (val2 + (sreg & 1) > val1 ? 1 : 0);
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= ((val1 ^ val2) & (val1 ^ R) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (1 & ((~val1 & val2) | (val2 & R) | (R & ~val1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteSbci(Cpu cpu, int opcode)
    {
        /* SBCI, 0100 KKKK dddd KKKK */
        int val1 = cpu.Data[((opcode & 0xf0) >> 4) + 16];
        var val2 = (opcode & 0xf) | ((opcode & 0xf00) >> 4);
        int sreg = cpu.Data[95];
        var R = val1 - val2 - (sreg & 1);
        cpu.Data[((opcode & 0xf0) >> 4) + 16] = (byte)R;
        sreg = (sreg & 0xc0) | (R == 0 && ((sreg >> 1) & 1) != 0 ? 2 : 0) | (val2 + (sreg & 1) > val1 ? 1 : 0);
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= ((val1 ^ val2) & (val1 ^ R) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (1 & ((~val1 & val2) | (val2 & R) | (R & ~val1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteSbiw(Cpu cpu, int opcode)
    {
        /* SBIW, 1001 0111 KKdd KKKK */
        var i = 2 * ((opcode & 0x30) >> 4) + 24;
        var a = cpu.GetUint16(i);
        var l = (opcode & 0xf) | ((opcode & 0xc0) >> 2);
        var R = a - l;
        cpu.SetUint16(i, R);
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (0x8000 & R) != 0 ? 4 : 0;
        sreg |= (a & ~R & 0x8000) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= l > a ? 1 : 0;
        sreg |= (1 & ((~a & l) | (l & R) | (R & ~a))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
        cpu.Cycles++;
    }

    static void ExecuteSub(Cpu cpu, int opcode)
    {
        /* SUB, 0001 10rd dddd rrrr */
        int val1 = cpu.Data[(opcode & 0x1f0) >> 4];
        int val2 = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        var R = val1 - val2;
        cpu.Data[(opcode & 0x1f0) >> 4] = (byte)R;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= ((val1 ^ val2) & (val1 ^ R) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= val2 > val1 ? 1 : 0;
        sreg |= (1 & ((~val1 & val2) | (val2 & R) | (R & ~val1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteSubi(Cpu cpu, int opcode)
    {
        /* SUBI, 0101 KKKK dddd KKKK */
        int val1 = cpu.Data[((opcode & 0xf0) >> 4) + 16];
        var val2 = (opcode & 0xf) | ((opcode & 0xf00) >> 4);
        var R = val1 - val2;
        cpu.Data[((opcode & 0xf0) >> 4) + 16] = (byte)R;
        var sreg = cpu.Data[95] & 0xc0;
        sreg |= R != 0 ? 0 : 2;
        sreg |= (128 & R) != 0 ? 4 : 0;
        sreg |= ((val1 ^ val2) & (val1 ^ R) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= val2 > val1 ? 1 : 0;
        sreg |= (1 & ((~val1 & val2) | (val2 & R) | (R & ~val1))) != 0 ? 0x20 : 0;
        cpu.Data[95] = (byte)sreg;
    }

    static void ExecuteSwap(Cpu cpu, int opcode)
    {
        /* SWAP, 1001 010d dddd 0010 */
        var d = (opcode & 0x1f0) >> 4;
        int i = cpu.Data[d];
        cpu.Data[d] = (byte)(((15 & i) << 4) | ((240 & i) >> 4));
    }
}
