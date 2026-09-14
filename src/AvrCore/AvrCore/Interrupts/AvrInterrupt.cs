// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js interrupt.ts)

namespace AvrCore.Interrupts;

public static class AvrInterrupt
{
    public static void Execute(Cpu cpu, int addr)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        var sp = cpu.GetUint16(Cpu.StackPointerRegister);
        cpu.WriteData(sp, (byte)(cpu.PC & 0xff));
        cpu.WriteData(sp - 1, (byte)((cpu.PC >> 8) & 0xff));
        if (cpu.Pc22Bits)
        {
            cpu.WriteData(sp - 2, (byte)((cpu.PC >> 16) & 0xff));
        }
        cpu.SetUint16(Cpu.StackPointerRegister, sp - (cpu.Pc22Bits ? 3 : 2));
        cpu.SetStatusRegister((byte)(cpu.Data[Cpu.StatusRegister] & 0x7f));
        cpu.Cycles += 2;
        cpu.PC = addr;
    }
}
