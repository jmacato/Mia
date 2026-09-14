// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class AsicToneGeneratorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public void DspKeyToneRequiresACompleteCommandAndStopsOnRelease(byte code)
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var tone = new AsicToneGenerator(cpu, clock);
        cpu.WriteData(0x0840, 0x41);
        cpu.WriteData(0x08e2, 0xe0);
        cpu.WriteData(0x08e4, 0);
        cpu.WriteData(0x08e5, 2);
        cpu.WriteData(0x08e8, (byte)(0xe0 + code));
        cpu.WriteData(0x08e7, 0xc4);
        Assert.Equal(0, tone.CurrentState.DtmfCode);

        clock.AdvanceTo(13000);
        cpu.WriteData(0x08e9, 0x38);
        cpu.WriteData(0x08e9, 0x20);
        cpu.WriteData(0x08e2, 0x80);
        Assert.Equal(code, tone.CurrentState.DtmfCode);
        Assert.Equal(13000, tone.CurrentState.Cycle);
        Assert.Equal(0x41, tone.CurrentState.Control);

        clock.AdvanceTo(26000);
        cpu.WriteData(0x08e2, 0xe0);
        cpu.WriteData(0x08e8, 0xf1);
        cpu.WriteData(0x08e9, 0x38);
        Assert.Equal(0, tone.CurrentState.DtmfCode);
        Assert.Equal(26000, tone.CurrentState.Cycle);
        Assert.Equal(0x41, tone.CurrentState.Control);
    }

    [Fact]
    public void DspWritesToOtherAddressesDoNotStartKeyTones()
    {
        var cpu = CreateCpu();
        var tone = new AsicToneGenerator(cpu, new MiaSystemClock());
        cpu.WriteData(0x08e2, 0xe0);
        cpu.WriteData(0x08e4, 0x80);
        cpu.WriteData(0x08e5, 2);
        cpu.WriteData(0x08e7, 0xc4);
        cpu.WriteData(0x08e8, 0xe1);
        cpu.WriteData(0x08e9, 0x38);
        Assert.Equal(0, tone.CurrentState.DtmfCode);
    }

    [Fact]
    public void RegisterWritesPublishCompleteCycleStampedState()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var tone = new AsicToneGenerator(cpu, clock);
        var states = new List<AsicToneState>();
        tone.StateChanged += states.Add;

        clock.SynchronizeAvrCycles(12);
        cpu.WriteData(AsicToneGenerator.ReloadAddress, 0x8f);
        clock.SynchronizeAvrCycles(24);
        cpu.WriteData(AsicToneGenerator.WidthAddress, 0x04);
        clock.SynchronizeAvrCycles(36);
        cpu.WriteData(AsicToneGenerator.UnknownAddress, 0x12);
        clock.SynchronizeAvrCycles(48);
        cpu.WriteData(AsicToneGenerator.ControlAddress, 0x4a);

        Assert.Equal(
            [
                new(13, 0x00, 0x00, 0x8f, 0x00),
                new(26, 0x00, 0x04, 0x8f, 0x00),
                new(39, 0x00, 0x04, 0x8f, 0x12),
                new(52, 0x4a, 0x04, 0x8f, 0x12),
            ],
            states);
        Assert.Equal(states[^1], tone.CurrentState);
    }

    [Fact]
    public void MaskedWritePublishesTheEffectiveRetainedValue()
    {
        var cpu = CreateCpu();
        var tone = new AsicToneGenerator(cpu, new MiaSystemClock());
        cpu.Data[AsicToneGenerator.ControlAddress] = 0xa0;
        AsicToneState? observed = null;
        tone.StateChanged += state => observed = state;

        cpu.WriteData(AsicToneGenerator.ControlAddress, 0x0a, 0x0f);

        Assert.Equal(0xaa, cpu.Data[AsicToneGenerator.ControlAddress]);
        Assert.Equal(
            new AsicToneState(0, 0xaa, 0, 0, 0),
            Assert.IsType<AsicToneState>(observed));
    }

    [Fact]
    public void WriteWithoutAnEffectiveChangeDoesNotPublish()
    {
        var cpu = CreateCpu();
        var tone = new AsicToneGenerator(cpu, new MiaSystemClock());
        var eventCount = 0;
        tone.StateChanged += _ => eventCount++;

        cpu.WriteData(AsicToneGenerator.ReloadAddress, 0, 0xf0);

        Assert.Equal(0, eventCount);
        Assert.Equal(default, tone.CurrentState);
    }

    [Fact]
    public void ZeroControlWritePublishesMutedState()
    {
        var cpu = CreateCpu();
        var tone = new AsicToneGenerator(cpu, new MiaSystemClock());
        cpu.WriteData(AsicToneGenerator.ControlAddress, 0x80);
        AsicToneState? muted = null;
        tone.StateChanged += state => muted = state;

        cpu.WriteData(AsicToneGenerator.ControlAddress, 0);

        Assert.Equal(
            new AsicToneState(0, 0, 0, 0, 0),
            Assert.IsType<AsicToneState>(muted));
    }

    [Fact]
    public void ExistingWriteHookStillReceivesToneRegisterWrites()
    {
        var cpu = CreateCpu();
        var hookCalls = 0;
        cpu.WriteHooks[AsicToneGenerator.WidthAddress] = (_, _, _, _) =>
        {
            hookCalls++;
            return false;
        };
        var tone = new AsicToneGenerator(cpu, new MiaSystemClock());
        var eventCount = 0;
        tone.StateChanged += _ => eventCount++;

        cpu.WriteData(AsicToneGenerator.WidthAddress, 0x30);

        Assert.Equal(1, hookCalls);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void MachineOwnsAndExposesToneGenerator()
    {
        using var machine = new MiaMachine([0x00, 0x00], virtualSim: false);
        AsicToneState? observed = null;
        machine.ToneGenerator.StateChanged += state => observed = state;

        machine.Cpu.WriteData(AsicToneGenerator.ReloadAddress, 0x60);
        machine.Cpu.WriteData(AsicToneGenerator.WidthAddress, 0x30);
        machine.Cpu.WriteData(AsicToneGenerator.ControlAddress, 0x80);

        Assert.Equal(
            new AsicToneState(machine.Cycles, 0x80, 0x30, 0x60, 0),
            Assert.IsType<AsicToneState>(observed));
    }

    static Cpu CreateCpu() => new(new byte[0x100], 0x1000);
}
