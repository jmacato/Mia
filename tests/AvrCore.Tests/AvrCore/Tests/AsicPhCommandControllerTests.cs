// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicPhCommandControllerTests
{
    [Fact]
    public void InitializationCommandCompletesAfterEnable()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var controller = new AsicPhCommandController(cpu, clock);

        cpu.WriteData(AsicPhCommandController.CommandAddress,
            AsicPhCommandController.InitializationCommand);
        cpu.WriteData(AsicPhCommandController.ControlAddress,
            AsicPhCommandController.CommandEnable);

        Assert.Equal(0, cpu.ReadData(AsicPhCommandController.StatusAddress));
        Assert.Equal(1, controller.StartedCommands);
        Assert.Equal(0, controller.CompletedCommands);

        clock.AdvanceBy(AsicPhCommandController.CommandCompletionCycles);

        Assert.Equal(AsicPhCommandController.CommandComplete,
            cpu.ReadData(AsicPhCommandController.StatusAddress));
        Assert.Equal(1, controller.CompletedCommands);
    }

    [Fact]
    public void UnknownCommandFailsClosed()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var controller = new AsicPhCommandController(cpu, clock);

        cpu.WriteData(AsicPhCommandController.CommandAddress, 0x2d);
        cpu.WriteData(AsicPhCommandController.ControlAddress,
            AsicPhCommandController.CommandEnable);
        clock.AdvanceBy(AsicPhCommandController.CommandCompletionCycles);

        Assert.Equal(0, cpu.ReadData(AsicPhCommandController.StatusAddress));
        Assert.Equal(0, controller.StartedCommands);
    }

    [Fact]
    public void ChannelEncodeCapturesRecoveredInputAndRaisesDoneSource()
    {
        var cpu = new Cpu(new byte[4], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sink = new AsicPhCommandControllerTestsRecordingEncoderSink();
        var controller = new AsicPhCommandController(
            cpu,
            clock,
            interrupts,
            sink);
        const int inputAddress = 0x100848;
        var input = Enumerable.Range(0, 23).Select(value => (byte)value).ToArray();
        input.CopyTo(cpu.Data, inputAddress);
        var context = AsicPhCommandController.EncoderContextAddress;
        cpu.Data[context] = 9;
        cpu.Data[context + AsicPhCommandController.EncoderInputPointerOffset] = 0x48;
        cpu.Data[context + AsicPhCommandController.EncoderInputPointerOffset + 1] = 0x08;
        cpu.Data[context + AsicPhCommandController.EncoderInputPointerOffset + 2] = 0x10;
        cpu.Data[context + AsicPhCommandController.EncoderInputLengthOffset] = 23;

        cpu.WriteData(AsicPhCommandController.CommandAddress,
            AsicPhCommandController.ChannelEncodeCommand);
        cpu.WriteData(AsicPhCommandController.ControlAddress,
            AsicPhCommandController.CommandEnable);
        clock.AdvanceBy(AsicPhCommandController.CommandCompletionCycles);

        Assert.Equal(input, sink.Input);
        Assert.Equal((byte)9, sink.FirmwareState);
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelEncoderDoneSource));
        Assert.Equal(1, controller.StartedCommands);
        Assert.Equal(1, controller.CompletedCommands);
    }

    [Fact]
    public void ScheduledTrafficActionStartsArmedChannelEncode()
    {
        var cpu = new Cpu(new byte[4], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, interrupts));
        var sink = new AsicPhCommandControllerTestsRecordingEncoderSink();
        var controller = new AsicPhCommandController(
            cpu,
            clock,
            interrupts,
            sink,
            timeGenerator: timeGenerator);
        const int inputAddress = 0x100848;
        var input = Enumerable.Range(0, 23).Select(value => (byte)value).ToArray();
        input.CopyTo(cpu.Data, inputAddress);
        var context = AsicPhCommandController.EncoderContextAddress;
        cpu.Data[context] = 13;
        cpu.Data[context + AsicPhCommandController.EncoderInputPointerOffset] = 0x48;
        cpu.Data[context + AsicPhCommandController.EncoderInputPointerOffset + 1] = 0x08;
        cpu.Data[context + AsicPhCommandController.EncoderInputPointerOffset + 2] = 0x10;
        cpu.Data[context + AsicPhCommandController.EncoderInputLengthOffset] = 23;

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        cpu.WriteData(
            AsicPhCommandController.ControlAddress,
            AsicPhCommandController.ScheduledCommandEnable);
        cpu.WriteData(
            AsicPhCommandController.CommandAddress,
            AsicPhCommandController.ChannelEncodeCommand);
        const ushort quarterBit = 10;
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, (byte)quarterBit);
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, 0x00);
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionSelectorAddress,
            AsicTimeGenerator.ScheduledEncoderActionToken);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ScheduledEncoderActionToken);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);
        foreach (var value in new byte[] { 0x4d, 0x81, 0xdf })
        {
            cpu.WriteData(0x0881, value);
        }

        clock.AdvanceBy(
            quarterBit * AsicTimeGenerator.QuarterBitCycles +
            AsicPhCommandController.CommandCompletionCycles);

        Assert.Equal(input, sink.Input);
        Assert.Equal((byte)13, sink.FirmwareState);
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelEncoderDoneSource));
        Assert.Equal(1, controller.StartedCommands);
        Assert.Equal(1, controller.CompletedCommands);
    }

    [Theory]
    [InlineData(AsicPhCommandController.AcknowledgeCommand)]
    [InlineData(0x00)]
    public void AcknowledgeOrResetClearsCompletion(byte command)
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        _ = new AsicPhCommandController(cpu, clock);
        cpu.WriteData(AsicPhCommandController.CommandAddress,
            AsicPhCommandController.InitializationCommand);
        cpu.WriteData(AsicPhCommandController.ControlAddress,
            AsicPhCommandController.CommandEnable);
        clock.AdvanceBy(AsicPhCommandController.CommandCompletionCycles);

        cpu.WriteData(AsicPhCommandController.CommandAddress, command);

        Assert.Equal(0, cpu.ReadData(AsicPhCommandController.StatusAddress));
    }
}
