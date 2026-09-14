// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    static void ExecuteBclr(Cpu cpu, int opcode)
    {
        /* BCLR, 1001 0100 1sss 1000 */
        cpu.SetStatusRegister((byte)(
            cpu.Data[95] & ~(1 << ((opcode & 0x70) >> 4))));
    }

    static void ExecuteBld(Cpu cpu, int opcode)
    {
        /* BLD, 1111 100d dddd 0bbb */
        var b = opcode & 7;
        var d = (opcode & 0x1f0) >> 4;
        cpu.Data[d] = (byte)((~(1 << b) & cpu.Data[d]) | (((cpu.Data[95] >> 6) & 1) << b));
    }

    static void ExecuteBrbc(Cpu cpu, int opcode)
    {
        /* BRBC, 1111 01kk kkkk ksss */
        if ((cpu.Data[95] & (1 << (opcode & 7))) == 0)
        {
            cpu.PC = cpu.PC + (((opcode & 0x1f8) >> 3) - ((opcode & 0x200) != 0 ? 0x40 : 0));
            cpu.Cycles++;
        }
    }

    static void ExecuteBrbs(Cpu cpu, int opcode)
    {
        /* BRBS, 1111 00kk kkkk ksss */
        if ((cpu.Data[95] & (1 << (opcode & 7))) != 0)
        {
            cpu.PC = cpu.PC + (((opcode & 0x1f8) >> 3) - ((opcode & 0x200) != 0 ? 0x40 : 0));
            cpu.Cycles++;
        }
    }

    static void ExecuteBset(Cpu cpu, int opcode)
    {
        /* BSET, 1001 0100 0sss 1000 */
        cpu.SetStatusRegister((byte)(
            cpu.Data[95] | (1 << ((opcode & 0x70) >> 4))));
    }

    static void ExecuteBst(Cpu cpu, int opcode)
    {
        /* BST, 1111 101d dddd 0bbb */
        int d = cpu.Data[(opcode & 0x1f0) >> 4];
        var b = opcode & 7;
        cpu.Data[95] = (byte)((cpu.Data[95] & 0xbf) | (((d >> b) & 1) != 0 ? 0x40 : 0));
    }

    static void ExecuteCbi(Cpu cpu, int opcode)
    {
        /* CBI, 1001 1000 AAAA Abbb */
        var A = opcode & 0xf8;
        var b = opcode & 7;
        int R = cpu.ReadData((A >> 3) + 32);
        var mask = 1 << b;
        cpu.WriteData((A >> 3) + 32, (byte)(R & ~mask), (byte)mask);
    }

    static void ExecuteCpse(Cpu cpu, int opcode)
    {
        /* CPSE, 0001 00rd dddd rrrr */
        if (cpu.Data[(opcode & 0x1f0) >> 4] == cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)])
        {
            var nextOpcode = cpu.GetProgWord(cpu.PC + 1);
            var skipSize = IsTwoWordInstruction(nextOpcode) ? 2 : 1;
            cpu.PC += skipSize;
            cpu.Cycles += skipSize;
        }
    }

    static void ExecuteSbi(Cpu cpu, int opcode)
    {
        /* SBI, 1001 1010 AAAA Abbb */
        var target = ((opcode & 0xf8) >> 3) + 32;
        var mask = 1 << (opcode & 7);
        cpu.WriteData(target, (byte)(cpu.ReadData(target) | mask), (byte)mask);
        cpu.Cycles++;
    }

    static void ExecuteSbic(Cpu cpu, int opcode)
    {
        /* SBIC, 1001 1001 AAAA Abbb */
        var value = cpu.ReadData(((opcode & 0xf8) >> 3) + 32);
        if ((value & (1 << (opcode & 7))) == 0)
        {
            var nextOpcode = cpu.GetProgWord(cpu.PC + 1);
            var skipSize = IsTwoWordInstruction(nextOpcode) ? 2 : 1;
            cpu.Cycles += skipSize;
            cpu.PC += skipSize;
        }
    }

    static void ExecuteSbis(Cpu cpu, int opcode)
    {
        /* SBIS, 1001 1011 AAAA Abbb */
        var value = cpu.ReadData(((opcode & 0xf8) >> 3) + 32);
        if ((value & (1 << (opcode & 7))) != 0)
        {
            var nextOpcode = cpu.GetProgWord(cpu.PC + 1);
            var skipSize = IsTwoWordInstruction(nextOpcode) ? 2 : 1;
            cpu.Cycles += skipSize;
            cpu.PC += skipSize;
        }
    }

    static void ExecuteSbrc(Cpu cpu, int opcode)
    {
        /* SBRC, 1111 110r rrrr 0bbb */
        if ((cpu.Data[(opcode & 0x1f0) >> 4] & (1 << (opcode & 7))) == 0)
        {
            var nextOpcode = cpu.GetProgWord(cpu.PC + 1);
            var skipSize = IsTwoWordInstruction(nextOpcode) ? 2 : 1;
            cpu.Cycles += skipSize;
            cpu.PC += skipSize;
        }
    }

    static void ExecuteSbrs(Cpu cpu, int opcode)
    {
        /* SBRS, 1111 111r rrrr 0bbb */
        if ((cpu.Data[(opcode & 0x1f0) >> 4] & (1 << (opcode & 7))) != 0)
        {
            var nextOpcode = cpu.GetProgWord(cpu.PC + 1);
            var skipSize = IsTwoWordInstruction(nextOpcode) ? 2 : 1;
            cpu.Cycles += skipSize;
            cpu.PC += skipSize;
        }
    }
}
