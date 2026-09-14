// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicEqualizerTests
{
    [Fact]
    public void UnknownControlSequenceRemainsInert()
    {
        var (cpu, clock, interrupts, equalizer) = CreateEqualizer();

        ProgramPhase2(cpu, start: false);
        cpu.WriteData(AsicEqualizer.ControlAddress, 0x79);
        clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(0, equalizer.StartedCount);
        Assert.Equal(0, equalizer.CompletedCount);
        Assert.Equal(
            0,
            interrupts.GetRaisedCount(
                AsicInterruptController.EqualizerDoneSource));
    }

    [Fact]
    public void Phase1ReportsInvalidResultThroughNativeInterrupt()
    {
        var (cpu, clock, interrupts, equalizer) = CreateEqualizer();
        cpu.Data.AsSpan(
            AsicEqualizer.Phase1OutputAddress,
            AsicEqualizer.Phase1OutputLength).Fill(0x5a);

        ProgramPhase1(cpu);
        clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(1, equalizer.StartedCount);
        Assert.Equal(1, equalizer.Phase1StartedCount);
        Assert.Equal(0, equalizer.Phase2StartedCount);
        Assert.Equal(1, equalizer.CompletedCount);
        Assert.Equal(
            AsicEqualizer.InvalidResultMinimum,
            cpu.ReadData(AsicEqualizer.InvalidResultAddress));
        Assert.Equal(
            Enumerable.Repeat((byte)0x5a, AsicEqualizer.Phase1OutputLength),
            cpu.Data.AsSpan(
                AsicEqualizer.Phase1OutputAddress,
                AsicEqualizer.Phase1OutputLength).ToArray());
        Assert.Equal(
            1,
            interrupts.GetRaisedCount(
                AsicInterruptController.EqualizerDoneSource));
    }

    [Fact]
    public void Phase2ReportsInvalidResultWithoutAcknowledgingControl()
    {
        var (cpu, clock, interrupts, equalizer) = CreateEqualizer();
        cpu.Data.AsSpan(
            AsicEqualizer.Phase2OutputAddress,
            AsicEqualizer.Phase2OutputLength).Fill(0xa5);

        ProgramPhase2(cpu);
        clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(1, equalizer.StartedCount);
        Assert.Equal(0, equalizer.Phase1StartedCount);
        Assert.Equal(1, equalizer.Phase2StartedCount);
        Assert.Equal(1, equalizer.CompletedCount);
        Assert.Equal(
            AsicEqualizer.Phase2StartControl,
            cpu.ReadData(AsicEqualizer.ControlAddress));
        Assert.Equal(
            AsicEqualizer.InvalidResultMinimum,
            cpu.ReadData(AsicEqualizer.InvalidResultAddress));
        Assert.Equal(
            Enumerable.Repeat((byte)0xa5, AsicEqualizer.Phase2OutputLength),
            cpu.Data.AsSpan(
                AsicEqualizer.Phase2OutputAddress,
                AsicEqualizer.Phase2OutputLength).ToArray());
        Assert.Equal(
            1,
            interrupts.GetRaisedCount(
                AsicInterruptController.EqualizerDoneSource));
    }

    [Fact]
    public void MismatchedPhaseConfigurationRemainsInert()
    {
        var (cpu, clock, interrupts, equalizer) = CreateEqualizer();

        ProgramPhase2(cpu, start: false);
        cpu.WriteData(0x0926, 0x22);
        cpu.WriteData(
            AsicEqualizer.ControlAddress,
            AsicEqualizer.Phase2StartControl);
        clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(0, equalizer.StartedCount);
        Assert.Equal(0, equalizer.CompletedCount);
        Assert.Equal(
            0,
            interrupts.GetRaisedCount(
                AsicInterruptController.EqualizerDoneSource));
    }

    [Fact]
    public void ExplicitSourcePublishesAllResultRegisters()
    {
        var cpu = new Cpu(new byte[0x100], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var equalizer = new AsicEqualizer(
            cpu,
            clock,
            interrupts,
            new AsicEqualizerTestsFixedEqualizerSource(new(1, 2, 3, 4, 5, 6, 7)));

        ProgramPhase2(cpu);
        clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5, 6, 7 },
            cpu.Data.AsSpan(
                AsicEqualizer.FirstResultAddress,
                AsicEqualizer.LastResultAddress -
                    AsicEqualizer.FirstResultAddress + 1).ToArray());
        Assert.Equal(1, equalizer.CompletedCount);
    }

    [Fact]
    public void Phase2MeasurementVariantCompletesThroughNativeInterrupt()
    {
        var (cpu, clock, interrupts, equalizer) = CreateEqualizer();

        ProgramPhase2(
            cpu,
            inputAddress: AsicEqualizer.Phase2MeasurementInputAddress);
        clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(1, equalizer.Phase2StartedCount);
        Assert.Equal(1, equalizer.CompletedCount);
        Assert.Equal(
            1,
            interrupts.GetRaisedCount(
                AsicInterruptController.EqualizerDoneSource));
    }

    [Fact]
    public void MachineWiresPhase2CompletionToInterruptFabric()
    {
        using var machine = new MiaMachine(new byte[0x100]);

        ProgramPhase2(machine.Cpu);
        machine.Clock.AdvanceBy(AsicEqualizer.CompletionCycles);

        Assert.Equal(1, machine.Equalizer.StartedCount);
        Assert.Equal(1, machine.Equalizer.CompletedCount);
        Assert.Equal(
            1,
            machine.InterruptController.GetRaisedCount(
                AsicInterruptController.EqualizerDoneSource));
    }

    static (
        Cpu Cpu,
        MiaSystemClock Clock,
        AsicInterruptController Interrupts,
        AsicEqualizer Equalizer) CreateEqualizer()
    {
        var cpu = new Cpu(new byte[0x100], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        return (cpu, clock, interrupts, new(cpu, clock, interrupts));
    }

    static void ProgramPhase1(Cpu cpu)
    {
        cpu.WriteData(AsicEqualizer.ControlAddress, AsicEqualizer.ArmedControl);
        cpu.WriteData(0x0922, 0x4c);
        cpu.WriteData(0x0923, 0x00);
        cpu.WriteData(0x0924, 0x5f);
        cpu.WriteData(0x0925, 0x18);
        cpu.WriteData(
            AsicEqualizer.ControlAddress,
            AsicEqualizer.Phase1StartControl);
    }

    static void ProgramPhase2(
        Cpu cpu,
        bool start = true,
        int inputAddress = AsicEqualizer.Phase2InputAddress)
    {
        cpu.WriteData(AsicEqualizer.ControlAddress, AsicEqualizer.ArmedControl);
        cpu.WriteData(0x0922, (byte)inputAddress);
        cpu.WriteData(0x0923, (byte)(inputAddress >> 8));
        cpu.WriteData(0x0924, 0xbf);
        cpu.WriteData(0x0925, 0x18);
        cpu.WriteData(0x0926, 0x23);
        cpu.WriteData(0x0927, 0x06);
        if (start)
        {
            cpu.WriteData(
                AsicEqualizer.ControlAddress,
                AsicEqualizer.Phase2StartControl);
        }
    }
}
