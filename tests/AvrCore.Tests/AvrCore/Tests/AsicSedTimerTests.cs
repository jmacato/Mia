// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicSedTimerTests
{
    [Fact]
    public void TimerStartsOnlyWhenFirmwareEnablesHighPriorityInterrupts()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var controller = new AsicInterruptController(cpu);
        var timer = new AsicSedTimer(cpu, clock, controller);

        clock.AdvanceBy(AsicSedTimer.TickCycles + 1);

        Assert.False(timer.Enabled);
        Assert.Equal(0, timer.TickCount);
        Assert.Equal(-1, cpu.NextInterrupt);

        cpu.WriteData(AsicSedTimer.InterruptControlAddress, 0x60);
        clock.AdvanceBy(AsicSedTimer.TickCycles);

        Assert.True(timer.Enabled);
        Assert.Equal(1, timer.TickCount);
        Assert.Equal(AsicInterruptController.SedTimerSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
    }

    [Fact]
    public void TimerRunsPeriodicallyAndStopsWhenFirmwareDisablesIt()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var controller = new AsicInterruptController(cpu);
        var timer = new AsicSedTimer(cpu, clock, controller);
        cpu.WriteData(AsicSedTimer.InterruptControlAddress, 0x20);

        clock.AdvanceBy(AsicSedTimer.TickCycles * 3);

        Assert.Equal(3, timer.TickCount);
        Assert.Equal(3, controller.RaisedCount);

        cpu.WriteData(AsicSedTimer.InterruptControlAddress, 0x00);
        clock.AdvanceBy(AsicSedTimer.TickCycles * 2);

        Assert.False(timer.Enabled);
        Assert.Equal(3, timer.TickCount);
        Assert.Equal(3, controller.RaisedCount);
    }

    [Fact]
    public void MaskedControlWriteUsesTheEffectiveRegisterValue()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var controller = new AsicInterruptController(cpu);
        var timer = new AsicSedTimer(cpu, clock, controller);
        cpu.Data[AsicSedTimer.InterruptControlAddress] = 0x40;

        cpu.WriteData(AsicSedTimer.InterruptControlAddress, 0x60, 0x20);

        Assert.True(timer.Enabled);
        Assert.Equal(0x60, cpu.Data[AsicSedTimer.InterruptControlAddress]);
    }

    [Fact]
    public void DueTickExecutesOnItsDedicatedWorker()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var controller = new AsicInterruptController(cpu);
        using var worker = new MiaWorker("SED test peripheral");
        var timer = new AsicSedTimer(cpu, clock, controller, worker);

        cpu.WriteData(AsicSedTimer.InterruptControlAddress, 0x20);
        clock.AdvanceBy(AsicSedTimer.TickCycles);

        Assert.Equal(1, timer.TickCount);
        Assert.Equal(1, worker.CompletedWorkCount);
        Assert.NotEqual(Environment.CurrentManagedThreadId, worker.ThreadId);
    }

    static Cpu CreateCpu() => new(new byte[0x800000], 0x1000);

}
