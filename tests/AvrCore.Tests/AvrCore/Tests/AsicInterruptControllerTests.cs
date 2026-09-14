// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicInterruptControllerTests
{
    [Fact]
    public void NamesTheLinkTransmitSource()
    {
        Assert.Equal("LH_TX", AsicInterruptController.GetSourceName(
            AsicInterruptController.LinkTransmitSource));
    }

    [Theory]
    [InlineData(AsicInterruptController.KeypadSource, "KEYB_INT")]
    [InlineData(AsicInterruptController.I2c1Source, "I2C1_INT")]
    [InlineData(AsicInterruptController.RtcSource, "RTC")]
    [InlineData(AsicInterruptController.SimRxSource, "SIMRX")]
    [InlineData(AsicInterruptController.ChannelDecoderDoneSource, "HW_ChannelDecoder_Done")]
    [InlineData(AsicInterruptController.ChannelEncoderDoneSource, "HW_ChannelEncoder_Done")]
    [InlineData(AsicInterruptController.EqualizerDoneSource, "HW_Equalizer_Done")]
    [InlineData(AsicInterruptController.SimRxLowSource, "SIMRX_LOW")]
    [InlineData(AsicInterruptController.LinkReceiveSource, "LH_RX")]
    [InlineData(AsicInterruptController.LinkTransmitSource, "LH_TX")]
    [InlineData(AsicInterruptController.SimTransmitSource, "SIMTX")]
    [InlineData(AsicInterruptController.I2cSource, "I2C_INT")]
    [InlineData(AsicInterruptController.SedTimerSource, "SED_Timer")]
    [InlineData(AsicInterruptController.FrameTickSource, "FrameTick_High_Priority")]
    public void FirmwareSourceNamesAreStable(byte source, string expected)
    {
        Assert.Equal(expected, AsicInterruptController.GetSourceName(source));
    }

    [Fact]
    public void HighPrioritySourceWaitsUntilInterruptsAreEnabled()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);

        controller.RaiseHighPriority(AsicInterruptController.FrameTickSource);
        cpu.Tick();

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(AsicInterruptController.FrameTickSource, cpu.Data[AsicInterruptController.SourceRegister]);

        cpu.Data[0x5f] = 0x80;
        cpu.Tick();

        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.PC);
        Assert.Equal(0x0ffd, cpu.SP);
        Assert.Equal(0, cpu.SREG & 0x80);
        Assert.Equal(1, controller.RaisedCount);
    }

    [Fact]
    public void ActiveSourceIsNotOverwrittenByAQueuedSource()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);

        controller.RaiseHighPriority(AsicInterruptController.I2c1Source);
        controller.RaiseHighPriority(AsicInterruptController.FrameTickSource);

        Assert.Equal(AsicInterruptController.I2c1Source, cpu.Data[AsicInterruptController.SourceRegister]);

        cpu.Data[0x5f] = 0x80;
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();

        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.PC);
        Assert.Equal(AsicInterruptController.I2c1Source, cpu.Data[AsicInterruptController.SourceRegister]);

        cpu.Data[0x5f] = 0x80;
        controller.PrepareForTick();

        Assert.Equal(AsicInterruptController.FrameTickSource, cpu.Data[AsicInterruptController.SourceRegister]);
        cpu.Tick();
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.PC);
        Assert.Equal(0x0ffa, cpu.SP);
        Assert.Equal(2, controller.RaisedCount);
    }

    [Fact]
    public void SeiEventPresentsPendingNestedSourceWithoutBoundaryPolling()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);
        controller.RaiseHighPriority(AsicInterruptController.I2c1Source);
        cpu.SetStatusRegister(0x80);
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();
        controller.RaiseHighPriority(AsicInterruptController.FrameTickSource);

        AvrInstruction.Execute(cpu, 0x9478); // SEI / BSET 7

        Assert.Equal(
            AsicInterruptController.FrameTickSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(
            AsicInterruptController.HighPriorityVectorWord,
            cpu.NextInterrupt);
    }

    [Fact]
    public void RepeatedPendingSourceIsCoalesced()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);

        controller.RaiseHighPriority(AsicInterruptController.FrameTickSource);
        controller.RaiseHighPriority(AsicInterruptController.FrameTickSource);
        cpu.Data[0x5f] = 0x80;
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();

        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.PC);
        Assert.Equal(-1, cpu.NextInterrupt);
        Assert.Equal(2, controller.RaisedCount);
    }

    [Fact]
    public void ActiveSourceWaitsForItsOwnReturnBeforeRedelivery()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);

        controller.RaiseHighPriority(AsicInterruptController.LinkReceiveSource);
        cpu.Data[0x5f] = 0x80;
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();

        controller.RaiseHighPriority(AsicInterruptController.LinkReceiveSource);
        cpu.Data[0x5f] = 0x80;
        controller.PrepareForTick();

        Assert.Equal(0x0ffd, cpu.SP);
        Assert.Equal(-1, cpu.NextInterrupt);

        controller.NotifyHighPriorityReturned();
        cpu.Tick();

        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.PC);
        Assert.Equal(0x0ffa, cpu.SP);
        Assert.Equal(2, controller.RaisedCount);
    }

    [Fact]
    public void ReturningNestedSourceKeepsOuterSourceInService()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);

        controller.RaiseHighPriority(AsicInterruptController.PhProcessSource);
        cpu.Data[0x5f] = 0x80;
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();

        controller.RaiseHighPriority(AsicInterruptController.PhDispatcherSource);
        cpu.Data[0x5f] = 0x80;
        controller.PrepareForTick();
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();

        controller.RaiseHighPriority(AsicInterruptController.PhProcessSource);
        cpu.Data[0x5f] = 0x80;
        controller.NotifyHighPriorityReturned();

        Assert.Equal(-1, cpu.NextInterrupt);
        Assert.Equal(0x0ffa, cpu.SP);

        controller.NotifyHighPriorityReturned();
        cpu.Tick();

        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.PC);
        Assert.Equal(0x0ff7, cpu.SP);
    }
}
