// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js interrupt.spec.ts)

using Xunit;

namespace AvrCore.Tests;

public class InterruptTests
{
    [Fact]
    public void ExecutesInterruptHandler()
    {
        var cpu = new Cpu(new byte[0x10000]);
        cpu.PC = 0x520;
        cpu.Data[94] = 0;
        cpu.Data[93] = 0x80; // SP <- 0x80
        cpu.Data[95] = 0b10000001; // SREG <- I------C
        AvrInterrupt.Execute(cpu, 5);
        Assert.Equal(2, cpu.Cycles);
        Assert.Equal(5, cpu.PC);
        Assert.Equal(0x7e, cpu.Data[93]); // SP
        Assert.Equal(0x20, cpu.Data[0x80]); // Return addr low
        Assert.Equal(0x5, cpu.Data[0x7f]); // Return addr high
        Assert.Equal(0b00000001, cpu.Data[95]); // SREG: -------C
    }

    [Fact]
    public void PushesThreeByteReturnAddressIn22BitPcMode()
    {
        var cpu = new Cpu(new byte[0x100000]);
        Assert.True(cpu.Pc22Bits);

        cpu.PC = 0x10520;
        cpu.Data[94] = 0;
        cpu.Data[93] = 0x80; // SP <- 0x80
        cpu.Data[95] = 0b10000001; // SREG <- I------C

        AvrInterrupt.Execute(cpu, 5);
        Assert.Equal(2, cpu.Cycles);
        Assert.Equal(5, cpu.PC);
        Assert.Equal(0x7d, cpu.Data[93]); // SP should decrement by 3
        Assert.Equal(0x20, cpu.Data[0x80]); // Return addr low
        Assert.Equal(0x05, cpu.Data[0x7f]); // Return addr high
        Assert.Equal(0x1, cpu.Data[0x7e]); // Return addr extended
        Assert.Equal(0b00000001, cpu.Data[95]); // SREG: -------C
    }

    [Fact]
    public void ReturnAddressWritesUseDataBusHooks()
    {
        var cpu = new Cpu(new byte[0x10000]);
        cpu.PC = 0x520;
        cpu.SP = 0x80;
        var writes = new List<(int Address, byte Value)>();
        foreach (var address in new[] { 0x80, 0x7f })
        {
            cpu.WriteHooks[address] = (value, _, hookAddress, _) =>
            {
                writes.Add((hookAddress, value));
                return false;
            };
        }

        AvrInterrupt.Execute(cpu, 5);

        Assert.Equal([(0x80, (byte)0x20), (0x7f, (byte)0x05)], writes);
    }
}
