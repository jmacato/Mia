// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicChannelDecoderTests
{
    [Fact]
    public void NoSignalSchCompletionFailsThroughNativeStatusAndSource()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var decoder = new AsicChannelDecoder(cpu, clock, interrupts);
        cpu.Data.AsSpan(
            AsicChannelDecoder.SchOutputAddress,
            AsicChannelDecoder.SchOutputLength).Fill(0x5a);

        StartSch(cpu);

        Assert.Equal(1, decoder.StartedCount);
        Assert.Equal(0, decoder.CompletedCount);
        clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);

        Assert.Equal(1, decoder.CompletedCount);
        Assert.Equal(0, decoder.SuccessfulCount);
        Assert.Equal(
            AsicChannelDecoder.FailureMask,
            cpu.ReadData(AsicChannelDecoder.StatusAddress) &
                AsicChannelDecoder.FailureMask);
        Assert.Equal(AsicChannelDecoder.SchCommand,
            cpu.ReadData(AsicChannelDecoder.CommandAddress));
        Assert.Equal(
            Enumerable.Repeat((byte)0x5a, AsicChannelDecoder.SchOutputLength),
            cpu.Data.AsSpan(
                AsicChannelDecoder.SchOutputAddress,
                AsicChannelDecoder.SchOutputLength).ToArray());
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    [Fact]
    public void SuccessfulSchCompletionCopiesOnlySourceDerivedBytes()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var source = new AsicChannelDecoderTestsCapturingSource([0x11, 0x22, 0x33, 0x44]);
        var decoder = new AsicChannelDecoder(
            cpu,
            clock,
            interrupts,
            source);
        var input = Enumerable.Range(0, AsicChannelDecoder.SchInputLength)
            .Select(value => (byte)value)
            .ToArray();
        input.CopyTo(cpu.Data, AsicChannelDecoder.SchInputAddress);
        cpu.Data[AsicChannelDecoder.StatusAddress] = 0xff;

        StartSch(cpu);
        input[0] = 0xee;
        clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);

        Assert.Equal(1, decoder.SuccessfulCount);
        Assert.NotNull(source.Input);
        Assert.Equal((byte)0, source.Input![0]);
        Assert.Equal(
            new byte[] { 0x11, 0x22, 0x33, 0x44 },
            cpu.Data.AsSpan(
                AsicChannelDecoder.SchOutputAddress,
                AsicChannelDecoder.SchOutputLength).ToArray());
        Assert.Equal(0,
            cpu.Data[AsicChannelDecoder.StatusAddress] &
                AsicChannelDecoder.FailureMask);
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    [Fact]
    public void NoSignalControlChannelCompletionFailsAndPreservesOutput()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var decoder = new AsicChannelDecoder(cpu, clock, interrupts);
        cpu.Data.AsSpan(
            AsicChannelDecoder.ControlChannelOutputAddress,
            AsicChannelDecoder.ControlChannelOutputLength).Fill(0x5a);

        StartControlChannel(cpu);
        clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);

        Assert.Equal(1, decoder.StartedCount);
        Assert.Equal(0, decoder.SchStartedCount);
        Assert.Equal(1, decoder.ControlChannelStartedCount);
        Assert.Equal(1, decoder.CompletedCount);
        Assert.Equal(0, decoder.SuccessfulCount);
        Assert.Equal(
            AsicChannelDecoder.FailureMask,
            cpu.ReadData(AsicChannelDecoder.StatusAddress) &
                AsicChannelDecoder.FailureMask);
        Assert.Equal(
            Enumerable.Repeat(
                (byte)0x5a,
                AsicChannelDecoder.ControlChannelOutputLength),
            cpu.Data.AsSpan(
                AsicChannelDecoder.ControlChannelOutputAddress,
                AsicChannelDecoder.ControlChannelOutputLength).ToArray());
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    [Fact]
    public void SuccessfulControlChannelCompletionUsesCapturedInput()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var result = Enumerable.Range(
                0x80,
                AsicChannelDecoder.ControlChannelOutputLength)
            .Select(value => (byte)value)
            .ToArray();
        var source = new AsicChannelDecoderTestsCapturingSource([], result);
        var decoder = new AsicChannelDecoder(cpu, clock, interrupts, source);
        var input = Enumerable.Range(
                0,
                AsicChannelDecoder.ControlChannelInputLength)
            .Select(value => (byte)value)
            .ToArray();
        input.CopyTo(cpu.Data, AsicChannelDecoder.ControlChannelInputAddress);
        cpu.Data[AsicChannelDecoder.FirmwareStateAddress] = 7;
        cpu.Data[AsicChannelDecoder.StatusAddress] = 0xff;

        StartControlChannel(cpu);
        cpu.Data[AsicChannelDecoder.ControlChannelInputAddress] = 0xee;
        clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);

        Assert.Equal(1, decoder.SuccessfulCount);
        Assert.NotNull(source.ControlChannelInput);
        Assert.Equal((byte)0, source.ControlChannelInput![0]);
        Assert.Equal((byte)7, source.ControlChannelFirmwareState);
        Assert.Equal(
            result,
            cpu.Data.AsSpan(
                AsicChannelDecoder.ControlChannelOutputAddress,
                AsicChannelDecoder.ControlChannelOutputLength).ToArray());
        Assert.Equal(
            0,
            cpu.Data[AsicChannelDecoder.StatusAddress] &
                AsicChannelDecoder.FailureMask);
    }

    [Fact]
    public void ScheduledTrafficDecoderPublishesDedicatedFacchLatchAndInterrupt()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, interrupts));
        var result = Enumerable.Range(
                0x80,
                AsicChannelDecoder.ControlChannelOutputLength)
            .Select(value => (byte)value)
            .ToArray();
        var source = new AsicChannelDecoderTestsCapturingSource([], result);
        var decoder = new AsicChannelDecoder(
            cpu,
            clock,
            interrupts,
            source,
            timeGenerator: timeGenerator);
        var input = Enumerable.Range(
                0,
                AsicChannelDecoder.ControlChannelInputLength)
            .Select(value => (byte)value)
            .ToArray();
        input.CopyTo(cpu.Data, AsicChannelDecoder.ControlChannelInputAddress);
        cpu.Data.AsSpan(
            AsicChannelDecoder.ControlChannelOutputAddress,
            AsicChannelDecoder.ControlChannelOutputLength).Fill(0x5a);
        cpu.Data.AsSpan(
            AsicChannelDecoder.TrafficFacchOutputAddress,
            AsicChannelDecoder.TrafficFacchOutputLength).Fill(0x6b);
        cpu.Data[AsicChannelDecoder.FirmwareStateAddress] =
            AsicChannelDecoder.TrafficFacchFirmwareState;
        cpu.Data[AsicChannelDecoder.StatusAddress] = 0xff;

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        const ushort quarterBit = 10;
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionDataAddress,
            (byte)quarterBit);
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionDataAddress,
            (byte)(quarterBit >> 8));
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionSelectorAddress,
            AsicTimeGenerator.ScheduledEncoderActionToken);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ScheduledDecoderActionToken);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);
        WriteTimeGeneratorDescriptor(cpu, 0x0881);

        clock.AdvanceBy(
            quarterBit * AsicTimeGenerator.QuarterBitCycles +
            AsicChannelDecoder.CompletionCycles);

        AssertScheduledFacchResult(cpu, source, decoder, result);
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    static void AssertScheduledFacchResult(
        Cpu cpu,
        AsicChannelDecoderTestsCapturingSource source,
        AsicChannelDecoder decoder,
        byte[] result)
    {
        Assert.Equal(1, decoder.ScheduledTrafficFacchStartedCount);
        Assert.Equal(1, decoder.ControlChannelStartedCount);
        Assert.Equal(
            AsicChannelDecoder.TrafficFacchFirmwareState,
            source.ControlChannelFirmwareState);
        Assert.Equal((byte)0, source.ControlChannelInput![0]);
        Assert.Equal(
            result,
            cpu.Data.AsSpan(
                AsicChannelDecoder.TrafficFacchOutputAddress,
                AsicChannelDecoder.TrafficFacchOutputLength).ToArray());
        Assert.Equal(
            Enumerable.Repeat(
                (byte)0x5a,
                AsicChannelDecoder.ControlChannelOutputLength),
            cpu.Data.AsSpan(
                AsicChannelDecoder.ControlChannelOutputAddress,
                AsicChannelDecoder.ControlChannelOutputLength).ToArray());
        Assert.Equal(1, cpu.Data[AsicChannelDecoder.TrafficFacchReadyAddress]);
        Assert.Equal(
            0,
            cpu.Data[AsicChannelDecoder.StatusAddress] &
                AsicChannelDecoder.FailureMask);
    }

    [Fact]
    public void LateControlChannelResultLatchesSourceDerivedStatusWithoutAnotherIrq()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var result = Enumerable.Range(
                0x80,
                AsicChannelDecoder.ControlChannelOutputLength)
            .Select(value => (byte)value)
            .ToArray();
        var source = new AsicChannelDecoderTestsCapturingSource(
            [],
            result,
            lateControlChannelResultCycles: 17);
        _ = new AsicChannelDecoder(cpu, clock, interrupts, source);
        cpu.Data[AsicChannelDecoder.ResultFailureAddress] = 1;

        StartControlChannel(cpu);
        clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);
        cpu.Data[AsicChannelDecoder.ResultFailureAddress] = 1;

        clock.AdvanceBy(17);

        Assert.Equal(0, cpu.Data[AsicChannelDecoder.ResultFailureAddress]);
        Assert.Equal(
            result,
            cpu.Data.AsSpan(
                AsicChannelDecoder.ControlChannelOutputAddress,
                AsicChannelDecoder.ControlChannelOutputLength).ToArray());
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    [Fact]
    public void MachineRoutesDecoderMmioToItsWorkerAndInjectedSource()
    {
        var source = new AsicChannelDecoderTestsCapturingSource([0xaa, 0xbb, 0xcc, 0xdd]);
        using var machine = new MiaMachine(
            [0x00, 0x00],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            channelDecoderSource: source);

        StartSch(machine.Cpu);
        machine.Clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);

        Assert.Equal(1, machine.ChannelDecoder.CompletedCount);
        Assert.Equal(
            new byte[] { 0xaa, 0xbb, 0xcc, 0xdd },
            machine.Cpu.Data.AsSpan(
                AsicChannelDecoder.SchOutputAddress,
                AsicChannelDecoder.SchOutputLength).ToArray());
        Assert.Equal(1, machine.InterruptController.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    [Theory]
    [InlineData(0xab, 0x50, 0x52)]
    [InlineData(0xac, 0x00, 0x52)]
    [InlineData(0xac, 0x50, 0x51)]
    public void UnknownCommandOrControlSequenceIsInert(
        byte command,
        byte firstControl,
        byte secondControl)
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var decoder = new AsicChannelDecoder(cpu, clock, interrupts);

        cpu.WriteData(AsicChannelDecoder.CommandAddress, command);
        cpu.WriteData(AsicChannelDecoder.ControlAddress, firstControl);
        cpu.WriteData(AsicChannelDecoder.ControlAddress, secondControl);
        clock.AdvanceBy(AsicChannelDecoder.CompletionCycles);

        Assert.Equal(0, decoder.StartedCount);
        Assert.Equal(0, interrupts.GetRaisedCount(
            AsicInterruptController.ChannelDecoderDoneSource));
    }

    static Cpu CreateCpu() => new(
        new byte[0x100],
        AsicChannelDecoder.ControlChannelInputAddress +
            AsicChannelDecoder.ControlChannelInputLength + 0x100);

    static void StartSch(Cpu cpu)
    {
        cpu.WriteData(
            AsicChannelDecoder.CommandAddress,
            AsicChannelDecoder.SchCommand);
        cpu.WriteData(
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.SchArmedControl);
        cpu.WriteData(
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.SchStartControl);
    }

    static void StartControlChannel(Cpu cpu)
    {
        cpu.WriteData(AsicChannelDecoder.ControlAddress, 0x01);
        cpu.WriteData(AsicChannelDecoder.ControlAddress, 0x00);
        cpu.WriteData(AsicChannelDecoder.CommandAddress, 0x80);
        cpu.WriteData(
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.ControlChannelArmedControl);
        cpu.WriteData(
            AsicChannelDecoder.CommandAddress,
            AsicChannelDecoder.SchCommand);
        _ = cpu.ReadData(AsicChannelDecoder.ControlAddress);
        cpu.WriteData(
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.ControlChannelStartControl);
    }

    static void WriteTimeGeneratorDescriptor(Cpu cpu, int address)
    {
        cpu.WriteData(address, 0x4d);
        cpu.WriteData(address, 0x81);
        cpu.WriteData(address, 0xdf);
    }
}
