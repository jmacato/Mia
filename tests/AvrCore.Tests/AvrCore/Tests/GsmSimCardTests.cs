// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class GsmSimCardTests
{
    [Fact]
    public void ActivationAndSelectUseCharacterSpacedT0Traffic()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(
            cpu,
            clock,
            interrupts,
            transmitCompletionCycles: 1);
        var card = new GsmSimCard(sim);

        cpu.WriteData(AsicSimInterface.ControlAddress, 0x04);
        Advance(clock, GsmSimCard.AtrStartDelayCycles);
        Assert.Equal(0x3b, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x00, cpu.ReadData(AsicSimInterface.DataAddress));

        Send(cpu, clock, 0xa0, 0xa4, 0x00, 0x00, 0x02);
        Advance(clock, 1);
        Assert.Equal(0xa4, cpu.ReadData(AsicSimInterface.DataAddress));

        Send(cpu, clock, 0x7f, 0x20);
        Advance(clock, 1);
        Assert.Equal(0x9f, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x17, cpu.ReadData(AsicSimInterface.DataAddress));

        Send(cpu, clock, 0xa0, 0xc0, 0x00, 0x00, 0x17);
        var response = new List<byte>();
        for (var index = 0; index < 26; index++)
        {
            Advance(clock, 1);
            response.Add(cpu.ReadData(AsicSimInterface.DataAddress));
        }

        Assert.Equal(0xc0, response[0]);
        Assert.Equal(new byte[] { 0x7f, 0x20 }, response.Skip(5).Take(2));
        Assert.Equal(new byte[] { 0x90, 0x00 }, response.TakeLast(2));
        Assert.Equal(1, card.ActivationCount);
        Assert.Equal(2, card.CommandCount);
    }

    [Fact]
    public void PhaseFileCanBeSelectedAndRead()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(
            cpu,
            clock,
            interrupts,
            transmitCompletionCycles: 1);
        _ = new GsmSimCard(sim);

        Send(cpu, clock, 0xa0, 0xa4, 0x00, 0x00, 0x02);
        Advance(clock, 1);
        Assert.Equal(0xa4, cpu.ReadData(AsicSimInterface.DataAddress));
        Send(cpu, clock, 0x7f, 0x20);
        Advance(clock, 1);
        Assert.Equal(0x9f, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x17, cpu.ReadData(AsicSimInterface.DataAddress));

        Send(cpu, clock, 0xa0, 0xa4, 0x00, 0x00, 0x02);
        Advance(clock, 1);
        Assert.Equal(0xa4, cpu.ReadData(AsicSimInterface.DataAddress));
        Send(cpu, clock, 0x6f, 0xae);
        Advance(clock, 1);
        Assert.Equal(0x9f, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x0f, cpu.ReadData(AsicSimInterface.DataAddress));

        Send(cpu, clock, 0xa0, 0xc0, 0x00, 0x00, 0x0f);
        for (var index = 0; index < 18; index++)
        {
            Advance(clock, 1);
            _ = cpu.ReadData(AsicSimInterface.DataAddress);
        }

        Send(cpu, clock, 0xa0, 0xb0, 0x00, 0x00, 0x01);
        Advance(clock, 1);
        Assert.Equal(0xb0, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x02, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x90, cpu.ReadData(AsicSimInterface.DataAddress));
        Advance(clock, 1);
        Assert.Equal(0x00, cpu.ReadData(AsicSimInterface.DataAddress));
    }

    static void Send(Cpu cpu, MiaSystemClock clock, params byte[] values)
    {
        foreach (var value in values)
        {
            cpu.WriteData(AsicSimInterface.DataAddress, value);
            Advance(clock, 1);
        }
    }

    static void Advance(MiaSystemClock clock, int cycles) => clock.AdvanceBy(cycles);
}
