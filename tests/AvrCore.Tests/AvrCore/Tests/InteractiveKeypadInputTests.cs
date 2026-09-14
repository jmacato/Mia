// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class InteractiveKeypadInputTests
{
    [Fact]
    public void QuickClickRemainsClosedThroughFirmwareDebounceReads()
    {
        var (cpu, keypad, _, interrupts, input) = CreateInput();
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);

        Assert.Equal(
            InteractiveKeypadTransition.Applied,
            input.SetKey(0x0b, 0x02, pressed: true));
        Assert.Equal(
            InteractiveKeypadTransition.Deferred,
            input.SetKey(0x0b, 0x02, pressed: false));

        Assert.Equal(0x1d, cpu.ReadData(AsicKeypad.RowStateAddress));
        input.FlushDeferredReleases();
        Assert.Equal(0x1d, cpu.ReadData(AsicKeypad.RowStateAddress));
        input.FlushDeferredReleases();

        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
        Assert.Equal(2, interrupts.RaisedCount);
    }

    [Fact]
    public void ObservedReleaseCommitsAfterCurrentInterruptSample()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var interrupts = new AsicInterruptController(cpu);
        var keypad = new AsicKeypad(cpu);
        var ports = new MiaPowerPortController(interrupts);
        var input = new InteractiveKeypadInput(
            keypad,
            ports,
            interrupts,
            cpu.ScheduleAfterTick);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        input.SetKey(0x0b, 0x02, pressed: true);
        input.SetKey(0x0b, 0x02, pressed: false);

        Assert.Equal(0x1d, cpu.ReadData(AsicKeypad.RowStateAddress));
        cpu.Tick();
        Assert.Equal(0x1d, cpu.ReadData(AsicKeypad.RowStateAddress));
        Assert.Equal(1, interrupts.RaisedCount);

        cpu.Tick();

        Assert.Equal(2, interrupts.RaisedCount);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void UnrelatedFirmwareReadDoesNotReleaseAQuickClick()
    {
        var (cpu, _, _, _, input) = CreateInput();

        input.SetKey(0x0b, 0x02, pressed: true);
        input.SetKey(0x0b, 0x02, pressed: false);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0e);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
        input.FlushDeferredReleases();

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(0x1d, cpu.ReadData(AsicKeypad.RowStateAddress));
        input.FlushDeferredReleases();
        Assert.Equal(0x1d, cpu.ReadData(AsicKeypad.RowStateAddress));
        input.FlushDeferredReleases();
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void ReleaseAfterFirmwareObservationIsImmediate()
    {
        var (cpu, _, _, interrupts, input) = CreateInput();
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x07);

        input.SetKey(0x07, 0x10, pressed: true);
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
        Assert.Equal(
            InteractiveKeypadTransition.Applied,
            input.SetKey(0x07, 0x10, pressed: false));

        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
        Assert.Equal(2, interrupts.RaisedCount);
    }

    [Fact]
    public void QuickPowerClickUsesBothRecoveredHardwareBoundaries()
    {
        var (cpu, _, ports, interrupts, input) = CreateInput();

        input.SetPower(pressed: true);
        Assert.True(ports.PowerPressed);
        Assert.Equal(
            InteractiveKeypadTransition.Deferred,
            input.SetPower(pressed: false));

        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.OnOffStatusCommand]);
        Assert.Equal(0x02, ports.ReadByte(MiaPowerPortController.ReadAddress));
        input.FlushDeferredReleases();
        Assert.True(ports.PowerPressed);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0f);
        Assert.Equal(0x1e, cpu.ReadData(AsicKeypad.RowStateAddress));

        input.FlushDeferredReleases();

        Assert.False(ports.PowerPressed);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
        Assert.Equal(2, interrupts.RaisedCount);
    }

    [Fact]
    public void QuickChordUsesOnePressAndOneReleaseInterrupt()
    {
        var (cpu, _, _, interrupts, input) = CreateInput();

        Assert.Equal(
            InteractiveKeypadTransition.Applied,
            input.SetKey(0x0e, 0x10, 0x0d, pressed: true));
        Assert.Equal(
            InteractiveKeypadTransition.Deferred,
            input.SetKey(0x0e, 0x10, 0x0d, pressed: false));
        Assert.Equal(1, interrupts.RaisedCount);

        foreach (var scanMask in new byte[] { 0x0e, 0x0d, 0x0e, 0x0d })
        {
            cpu.WriteData(AsicKeypad.ScanControlAddress, scanMask);
            Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
            input.FlushDeferredReleases();
            Assert.Equal(1, interrupts.RaisedCount);
        }

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0e);
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
        input.FlushDeferredReleases();

        Assert.Equal(2, interrupts.RaisedCount);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    static (
        Cpu Cpu,
        AsicKeypad Keypad,
        MiaPowerPortController Ports,
        AsicInterruptController Interrupts,
        InteractiveKeypadInput Input) CreateInput()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var interrupts = new AsicInterruptController(cpu);
        var keypad = new AsicKeypad(cpu);
        var ports = new MiaPowerPortController(interrupts);
        var input = new InteractiveKeypadInput(keypad, ports, interrupts);
        return (cpu, keypad, ports, interrupts, input);
    }
}
