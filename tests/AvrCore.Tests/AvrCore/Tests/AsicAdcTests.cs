// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicAdcTests
{
    [Fact]
    public void UnreadConversionsStayBoundedAndPreserveAHalfReadResult()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(cpu, clock, new AsicInterruptRouter(cpu, interrupts));
        using var adc = new AsicAdc(cpu, timeGenerator, interrupts, new AsicAdcTestsFixedSampleSource(0x1234));
        ProgramConversion(cpu, clock, actionId: 54, port: 0x0881);
        clock.AdvanceBy(240);
        cpu.WriteData(AsicAdc.SelectorAddress, 1 << 2);
        Assert.Equal(0x34, cpu.ReadData(AsicAdc.ResultStreamAddress));
        for (int i = 0; i < 1000; i++)
        {
            clock.AdvanceTo((clock.Cycles / 60000 + 1) * 60000);
            ProgramConversion(cpu, clock, actionId: 54, port: 0x0881);
            clock.AdvanceBy(240);
        }
        Assert.Equal(AsicAdc.MaximumPendingResults, adc.GetPendingResultCount(1));
        Assert.Equal(0x12, cpu.ReadData(AsicAdc.ResultStreamAddress));
        Assert.Equal(AsicAdc.MaximumPendingResults - 1, adc.GetPendingResultCount(1));
    }

    [Fact]
    public void FinalAdcActionEnqueuesTwoByteResultAndRaisesNativeSource()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, interrupts));
        using var adc = new AsicAdc(
            cpu,
            timeGenerator,
            interrupts,
            new AsicAdcTestsFixedSampleSource(0x1234));

        ProgramConversion(cpu, clock, actionId: 54, port: 0x0881);

        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 10);
        Assert.Equal(0, adc.CompletionCount);
        Assert.Equal(0, interrupts.GetRaisedCount(
            AsicInterruptController.AdcDoneSource));

        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 10);

        Assert.Equal(1, adc.CompletionCount);
        Assert.Equal(1, adc.GetPendingResultCount(1));
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.AdcDoneSource));

        cpu.WriteData(AsicAdc.SelectorAddress, 1 << 2);
        Assert.Equal(0x34, cpu.ReadData(AsicAdc.ResultStreamAddress));
        Assert.Equal(0x12, cpu.ReadData(AsicAdc.ResultStreamAddress));
        Assert.Equal(0, adc.GetPendingResultCount(1));
        Assert.Equal(2, adc.ResultReadCount);
    }

    [Fact]
    public void SelectorLocalInitializationWritesAreNotConversionResults()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            new MiaSystemClock(),
            new AsicInterruptRouter(cpu, interrupts));
        using var adc = new AsicAdc(cpu, timeGenerator, interrupts);

        cpu.WriteData(AsicAdc.SelectorAddress, 2 << 2);
        cpu.WriteData(AsicAdc.ResultStreamAddress, 0x00);
        cpu.WriteData(AsicAdc.ResultStreamAddress, 0xc0);
        cpu.WriteData(AsicAdc.SelectorAddress, 1 << 2);
        cpu.WriteData(AsicAdc.ResultStreamAddress, 0x55);

        Assert.Equal(0, adc.GetPendingResultCount(1));
        Assert.Equal(0, adc.GetPendingResultCount(2));
        Assert.Equal(0x55, cpu.ReadData(AsicAdc.ResultStreamAddress));
        cpu.WriteData(AsicAdc.SelectorAddress, 2 << 2);
        Assert.Equal(0xc0, cpu.ReadData(AsicAdc.ResultStreamAddress));
    }

    [Fact]
    public void DefaultSourceRepresentsNoSignalAsRawZero()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, interrupts));
        using var adc = new AsicAdc(cpu, timeGenerator, interrupts);

        ProgramConversion(cpu, clock, actionId: 52, port: 0x0882);
        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 20);

        cpu.WriteData(AsicAdc.SelectorAddress, 0);
        Assert.Equal(0, cpu.ReadData(AsicAdc.ResultStreamAddress));
        Assert.Equal(0, cpu.ReadData(AsicAdc.ResultStreamAddress));
    }

    [Fact]
    public void SchActionCompletesTheFirmwareSelectedAdc()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, interrupts));
        using var adc = new AsicAdc(
            cpu,
            timeGenerator,
            interrupts,
            new AsicAdcTestsFixedSampleSource(0x4567));

        cpu.WriteData(AsicAdc.SelectorAddress, 1 << 2);
        ProgramSingleConversion(
            cpu,
            clock,
            new(
                ActionId: AsicAdc.SchActionId,
                Operand: AsicAdc.SchActionOperand,
                Port: 0x0881));
        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 10);

        Assert.Equal(1, adc.CompletionCount);
        Assert.Equal(1, adc.GetPendingResultCount(1));
        Assert.Equal(1, interrupts.GetRaisedCount(
            AsicInterruptController.AdcDoneSource));
        Assert.Equal(0x67, cpu.ReadData(AsicAdc.ResultStreamAddress));
        Assert.Equal(0x45, cpu.ReadData(AsicAdc.ResultStreamAddress));
    }

    [Fact]
    public void OtherActionEightOperandsDoNotCompleteTheAdc()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, interrupts));
        using var adc = new AsicAdc(cpu, timeGenerator, interrupts);

        cpu.WriteData(AsicAdc.SelectorAddress, 3 << 2);
        ProgramSingleConversion(
            cpu,
            clock,
            new(ActionId: 8, Operand: 568, Port: 0x0881));
        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 10);

        Assert.Equal(0, adc.CompletionCount);
        Assert.Equal(0, interrupts.GetRaisedCount(
            AsicInterruptController.AdcDoneSource));
    }

    static void ProgramConversion(
        Cpu cpu,
        MiaSystemClock clock,
        byte actionId,
        int port)
    {
        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        foreach (var quarterBit in new ushort[] { 10, 20 })
        {
            cpu.WriteData(
                AsicTimeGenerator.ActionDefinitionDataAddress,
                (byte)quarterBit);
            cpu.WriteData(
                AsicTimeGenerator.ActionDefinitionDataAddress,
                (byte)(quarterBit >> 8));
        }
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionSelectorAddress,
            unchecked((byte)(actionId + 1)));
        var operand = 657;
        var word = (operand << 6) | actionId;
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, (byte)word);
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, (byte)(word >> 8));
        cpu.WriteData(
            AsicTimeGenerator.ActionSelectorAddress,
            (byte)(port - AsicTimeGenerator.FirstActionSchedulePortAddress));
        cpu.WriteData(port, 0x4d);
        cpu.WriteData(port, 0x81);
        cpu.WriteData(port, 0xdf);
    }

    static void ProgramSingleConversion(
        Cpu cpu,
        MiaSystemClock clock,
        AdcProgram program)
    {
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
            unchecked((byte)(program.ActionId + 1)));
        var word = (program.Operand << 6) | program.ActionId;
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, (byte)word);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            (byte)(word >> 8));
        cpu.WriteData(
            AsicTimeGenerator.ActionSelectorAddress,
            (byte)(program.Port - AsicTimeGenerator.FirstActionSchedulePortAddress));
        cpu.WriteData(program.Port, 0x4d);
        cpu.WriteData(program.Port, 0x81);
        cpu.WriteData(program.Port, 0xdf);
    }
}
