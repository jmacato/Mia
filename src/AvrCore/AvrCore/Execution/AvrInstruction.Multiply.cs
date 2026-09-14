// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    static void ExecuteFmul(Cpu cpu, int opcode)
    {
        /* FMUL, 0000 0011 0ddd 1rrr */
        int v1 = cpu.Data[((opcode & 0x70) >> 4) + 16];
        int v2 = cpu.Data[(opcode & 7) + 16];
        var R = (v1 * v2) << 1;
        cpu.SetUint16(0, R);
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xfc) | ((0xffff & R) != 0 ? 0 : 2) | (((v1 * v2) & 0x8000) != 0 ? 1 : 0));
        cpu.Cycles++;
    }

    static void ExecuteFmuls(Cpu cpu, int opcode)
    {
        /* FMULS, 0000 0011 1ddd 0rrr */
        int v1 = cpu.GetInt8(((opcode & 0x70) >> 4) + 16);
        int v2 = cpu.GetInt8((opcode & 7) + 16);
        var R = (v1 * v2) << 1;
        cpu.SetUint16(0, R);
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xfc) | ((0xffff & R) != 0 ? 0 : 2) | (((v1 * v2) & 0x8000) != 0 ? 1 : 0));
        cpu.Cycles++;
    }

    static void ExecuteFmulsu(Cpu cpu, int opcode)
    {
        /* FMULSU, 0000 0011 1ddd 1rrr */
        int v1 = cpu.GetInt8(((opcode & 0x70) >> 4) + 16);
        int v2 = cpu.Data[(opcode & 7) + 16];
        var R = (v1 * v2) << 1;
        cpu.SetUint16(0, R);
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xfc) | ((0xffff & R) != 0 ? 0 : 2) | (((v1 * v2) & 0x8000) != 0 ? 1 : 0));
        cpu.Cycles++;
    }

    static void ExecuteMul(Cpu cpu, int opcode)
    {
        /* MUL, 1001 11rd dddd rrrr */
        var R = cpu.Data[(opcode & 0x1f0) >> 4] * cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        cpu.SetUint16(0, R);
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xfc) | ((0xffff & R) != 0 ? 0 : 2) | ((0x8000 & R) != 0 ? 1 : 0));
        cpu.Cycles++;
    }

    static void ExecuteMuls(Cpu cpu, int opcode)
    {
        /* MULS, 0000 0010 dddd rrrr */
        var R = cpu.GetInt8(((opcode & 0xf0) >> 4) + 16) * cpu.GetInt8((opcode & 0xf) + 16);
        cpu.SetUint16(0, R);
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xfc) | ((0xffff & R) != 0 ? 0 : 2) | ((0x8000 & R) != 0 ? 1 : 0));
        cpu.Cycles++;
    }

    static void ExecuteMulsu(Cpu cpu, int opcode)
    {
        /* MULSU, 0000 0011 0ddd 0rrr */
        var R = cpu.GetInt8(((opcode & 0x70) >> 4) + 16) * cpu.Data[(opcode & 7) + 16];
        cpu.SetUint16(0, R);
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xfc) | ((0xffff & R) != 0 ? 0 : 2) | ((0x8000 & R) != 0 ? 1 : 0));
        cpu.Cycles++;
    }
}
