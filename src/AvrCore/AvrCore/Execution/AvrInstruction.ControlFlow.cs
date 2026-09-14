// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    static void ExecuteCall(Cpu cpu, int opcode)
    {
        /* CALL, 1001 010k kkkk 111k kkkk kkkk kkkk kkkk */
        var k = cpu.GetProgWord(cpu.PC + 1) | ((opcode & 1) << 16) | ((opcode & 0x1f0) << 13);
        var ret = cpu.PC + 2;
        var sp = cpu.GetUint16(93);
        var pc22Bits = cpu.Pc22Bits;
        cpu.WriteData(sp, (byte)(255 & ret));
        cpu.WriteData(sp - 1, (byte)((ret >> 8) & 255));
        if (pc22Bits)
        {
            cpu.WriteData(sp - 2, (byte)((ret >> 16) & 255));
        }
        cpu.SetUint16(93, sp - (pc22Bits ? 3 : 2));
        cpu.PC = k - 1;
        cpu.Cycles += pc22Bits ? 4 : 3;
    }

    static void ExecuteEicall(Cpu cpu, int opcode)
    {
        /* EICALL, 1001 0101 0001 1001 */
        var retAddr = cpu.PC + 1;
        var sp = cpu.GetUint16(93);
        int eind = cpu.Data[0x5c];
        cpu.WriteData(sp, (byte)(retAddr & 255));
        cpu.WriteData(sp - 1, (byte)((retAddr >> 8) & 255));
        cpu.WriteData(sp - 2, (byte)((retAddr >> 16) & 255));
        cpu.SetUint16(93, sp - 3);
        cpu.PC = ((eind << 16) | cpu.GetUint16(30)) - 1;
        cpu.Cycles += 3;
    }

    static void ExecuteEijmp(Cpu cpu, int opcode)
    {
        /* EIJMP, 1001 0100 0001 1001 */
        int eind = cpu.Data[0x5c];
        cpu.PC = ((eind << 16) | cpu.GetUint16(30)) - 1;
        cpu.Cycles++;
    }

    static void ExecuteIcall(Cpu cpu, int opcode)
    {
        /* ICALL, 1001 0101 0000 1001 */
        var retAddr = cpu.PC + 1;
        var sp = cpu.GetUint16(93);
        var pc22Bits = cpu.Pc22Bits;
        cpu.WriteData(sp, (byte)(retAddr & 255));
        cpu.WriteData(sp - 1, (byte)((retAddr >> 8) & 255));
        if (pc22Bits)
        {
            cpu.WriteData(sp - 2, (byte)((retAddr >> 16) & 255));
        }
        cpu.SetUint16(93, sp - (pc22Bits ? 3 : 2));
        cpu.PC = cpu.GetUint16(30) - 1;
        cpu.Cycles += pc22Bits ? 3 : 2;
    }

    static void ExecuteIjmp(Cpu cpu, int opcode)
    {
        /* IJMP, 1001 0100 0000 1001 */
        cpu.PC = cpu.GetUint16(30) - 1;
        cpu.Cycles++;
    }

    static void ExecuteJmp(Cpu cpu, int opcode)
    {
        /* JMP, 1001 010k kkkk 110k kkkk kkkk kkkk kkkk */
        cpu.PC = (cpu.GetProgWord(cpu.PC + 1) | ((opcode & 1) << 16) | ((opcode & 0x1f0) << 13)) - 1;
        cpu.Cycles += 2;
    }

    static void ExecuteRcall(Cpu cpu, int opcode)
    {
        /* RCALL, 1101 kkkk kkkk kkkk */
        var k = (opcode & 0x7ff) - ((opcode & 0x800) != 0 ? 0x800 : 0);
        var retAddr = cpu.PC + 1;
        var sp = cpu.GetUint16(93);
        var pc22Bits = cpu.Pc22Bits;
        cpu.WriteData(sp, (byte)(255 & retAddr));
        cpu.WriteData(sp - 1, (byte)((retAddr >> 8) & 255));
        if (pc22Bits)
        {
            cpu.WriteData(sp - 2, (byte)((retAddr >> 16) & 255));
        }
        cpu.SetUint16(93, sp - (pc22Bits ? 3 : 2));
        cpu.PC += k;
        cpu.Cycles += pc22Bits ? 3 : 2;
    }

    static void ExecuteRet(Cpu cpu, int opcode)
    {
        /* RET, 1001 0101 0000 1000 */
        var pc22Bits = cpu.Pc22Bits;
        var i = cpu.GetUint16(93) + (pc22Bits ? 3 : 2);
        cpu.SetUint16(93, i);
        var returnAddress = (cpu.ReadData(i - 1) << 8) + cpu.ReadData(i);
        if (pc22Bits)
        {
            returnAddress |= cpu.ReadData(i - 2) << 16;
        }
        cpu.PC = returnAddress - 1;
        cpu.Cycles += pc22Bits ? 4 : 3;
    }

    static void ExecuteReti(Cpu cpu, int opcode)
    {
        /* RETI, 1001 0101 0001 1000 */
        var pc22Bits = cpu.Pc22Bits;
        var i = cpu.GetUint16(93) + (pc22Bits ? 3 : 2);
        cpu.SetUint16(93, i);
        var returnAddress = (cpu.ReadData(i - 1) << 8) + cpu.ReadData(i);
        if (pc22Bits)
        {
            returnAddress |= cpu.ReadData(i - 2) << 16;
        }
        cpu.PC = returnAddress - 1;
        cpu.Cycles += pc22Bits ? 4 : 3;
        cpu.SetStatusRegister((byte)(cpu.Data[95] | 0x80));
    }

    static void ExecuteRjmp(Cpu cpu, int opcode)
    {
        /* RJMP, 1100 kkkk kkkk kkkk */
        cpu.PC = cpu.PC + ((opcode & 0x7ff) - ((opcode & 0x800) != 0 ? 0x800 : 0));
        cpu.Cycles++;
    }
}
