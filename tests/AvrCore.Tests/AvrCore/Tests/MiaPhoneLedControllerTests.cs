// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaPhoneLedControllerTests
{
    const int PrimaryI2cDataAddress = 0x0839;

    [Fact]
    public void UnpluggedHandsfreeInputStaysHighAcrossSharedLightWrites()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        _ = new AsicHandsetPort(cpu);
        var ports = new MiaPowerPortController(new AsicInterruptController(cpu));
        using var leds = new MiaPhoneLedController(cpu, ports);

        cpu.WriteData(AsicHandsetPort.DataAddress, 0);
        Assert.Equal(0x40, cpu.ReadData(AsicHandsetPort.DataAddress));
        Assert.Equal(default, leds.State);

        cpu.WriteData(AsicHandsetPort.DataAddress, 0x18);
        Assert.Equal(0x58, cpu.ReadData(AsicHandsetPort.DataAddress));
        Assert.True(leds.State.RightBlue);
        Assert.True(leds.State.BacklightsOn);

        // The firmware temporarily changes the pin direction to sample it.
        cpu.WriteData(AsicHandsetPort.DirectionAddress, 0x40);
        Assert.Equal(0x18, cpu.ReadData(AsicHandsetPort.DataAddress));
        cpu.WriteData(AsicHandsetPort.DirectionAddress, 0);
        Assert.Equal(0x58, cpu.ReadData(AsicHandsetPort.DataAddress));
        cpu.WriteData(AsicHandsetPort.DataAddress, 0);
        Assert.Equal(0x40, cpu.ReadData(AsicHandsetPort.DataAddress));
        Assert.Equal(default, leds.State);
    }

    [Fact]
    public void StartsWithAllPhysicalLampsOff()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        using var leds = new MiaPhoneLedController(cpu, ports);

        Assert.Equal(default, leds.State);
    }

    [Fact]
    public void MmioWritesUseRecoveredGreenAndBluePolarities()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        using var leds = new MiaPhoneLedController(cpu, ports);
        var observations = new List<MiaPhoneLedState>();
        leds.StateChanged += observations.Add;

        cpu.WriteData(
            MiaPhoneLedController.GreenControlAddress,
            MiaPhoneLedController.GreenMask);
        cpu.WriteData(
            MiaPhoneLedController.GreenControlAddress,
            0,
            MiaPhoneLedController.GreenMask);
        cpu.WriteData(
            MiaPhoneLedController.BlueControlAddress,
            MiaPhoneLedController.BlueMask,
            MiaPhoneLedController.BlueMask);
        cpu.WriteData(
            MiaPhoneLedController.BlueControlAddress,
            0x80,
            0x80);
        cpu.WriteData(
            MiaPhoneLedController.GreenControlAddress,
            MiaPhoneLedController.GreenMask,
            MiaPhoneLedController.GreenMask);
        cpu.WriteData(
            MiaPhoneLedController.BlueControlAddress,
            0,
            MiaPhoneLedController.BlueMask);

        Assert.Equal(
        [
            new MiaPhoneLedState(false, true, false),
            new MiaPhoneLedState(false, true, true),
            new MiaPhoneLedState(false, false, true),
            default,
        ], observations);
        Assert.Equal(0x80,
            cpu.ReadData(MiaPhoneLedController.BlueControlAddress));
    }

    [Fact]
    public void SharedMmioTracksBothActiveHighIlluminationBits()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        using var leds = new MiaPhoneLedController(cpu, ports);
        var observations = new List<MiaPhoneLedState>();
        leds.StateChanged += observations.Add;

        cpu.WriteData(
            MiaPhoneLedController.SharedControlAddress,
            MiaPhoneLedController.IlluminationBit4Mask,
            MiaPhoneLedController.IlluminationBit4Mask);
        cpu.WriteData(
            MiaPhoneLedController.SharedControlAddress,
            MiaPhoneLedController.IlluminationBit1Mask,
            MiaPhoneLedController.IlluminationBit1Mask);
        cpu.WriteData(
            MiaPhoneLedController.SharedControlAddress,
            0,
            MiaPhoneLedController.IlluminationBit4Mask);
        cpu.WriteData(
            MiaPhoneLedController.SharedControlAddress,
            0,
            MiaPhoneLedController.IlluminationBit1Mask);

        Assert.Equal(
        [
            new MiaPhoneLedState(false, false, false, true, false),
            new MiaPhoneLedState(false, false, false, true, true),
            new MiaPhoneLedState(false, false, false, false, true),
            default,
        ], observations);
        Assert.True(observations[0].BacklightsOn);
        Assert.True(observations[2].BacklightsOn);
        Assert.False(leds.State.BacklightsOn);
        Assert.Equal(
            0,
            cpu.ReadData(MiaPhoneLedController.SharedControlAddress));
    }

    [Fact]
    public void PrimaryI2cPortA4BitSevenDrivesTheRedDie()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var ports = new MiaPowerPortController(interrupts);
        using var leds = new MiaPhoneLedController(cpu, ports);
        _ = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            PrimaryI2cDataAddress,
            AsicInterruptController.I2cSource,
            MiaPowerPortController.Acknowledges,
            ports.WriteTransaction);

        WriteRedControl(cpu, clock, 0x80);
        Assert.Equal(new MiaPhoneLedState(true, false, false), leds.State);

        WriteRedControl(cpu, clock, 0x00);
        Assert.Equal(default, leds.State);
    }

    static void WriteRedControl(
        Cpu cpu,
        MiaSystemClock clock,
        byte value)
    {
        StartTransfer(cpu, clock);
        WriteStep(cpu, clock, MiaPowerPortController.WriteAddress);
        WriteStep(cpu, clock, MiaPowerPortController.PortA4OutputCommand);
        WriteStep(cpu, clock, value);
        cpu.WriteData(PrimaryI2cDataAddress + 1, 0x98);
    }

    static void StartTransfer(Cpu cpu, MiaSystemClock clock)
    {
        cpu.WriteData(PrimaryI2cDataAddress + 1, 0xa0);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
    }

    static void WriteStep(
        Cpu cpu,
        MiaSystemClock clock,
        byte value)
    {
        cpu.WriteData(PrimaryI2cDataAddress, value);
        cpu.WriteData(PrimaryI2cDataAddress + 1, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
    }
}
