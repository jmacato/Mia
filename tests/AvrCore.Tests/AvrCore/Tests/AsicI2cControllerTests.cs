// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicI2cControllerTests
{
    [Fact]
    public void WriteTransferDrivesStandardStatusesAndDisplayTransaction()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var display = new S4595Display();
        var i2c = new AsicI2cController(cpu, clock, interrupts, display);

        StartTransfer(cpu, clock);
        Assert.Equal(0x08, cpu.ReadData(AsicI2cController.StatusAddress));
        WriteStep(cpu, clock, S4595Display.WriteAddress);
        Assert.Equal(0x18, cpu.ReadData(AsicI2cController.StatusAddress));
        Assert.Equal(0x90, cpu.ReadData(AsicI2cController.ControlAddress));
        WriteStep(cpu, clock, 0x04);
        Assert.Equal(0x28, i2c.Status);
        WriteStep(cpu, clock, 0x22);
        cpu.WriteData(AsicI2cController.ControlAddress, 0x98);
        cpu.WriteData(AsicI2cController.ControlAddress, 0x90);

        Assert.Equal(1, i2c.CompletedTransactions);
        Assert.Equal(1, display.TransactionCount);
        Assert.Equal(0x22, display.CursorColumn);
        Assert.Equal(4, i2c.CompletedSteps);
        Assert.Equal(0x80, cpu.ReadData(AsicI2cController.ControlAddress));
    }

    [Fact]
    public void StartCompletesThroughControllerInterrupt()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var i2c = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            new S4595Display());

        cpu.WriteData(AsicI2cController.ControlAddress, 0xa0);
        Assert.Equal(0xa0, cpu.ReadData(AsicI2cController.ControlAddress));
        clock.AdvanceBy(AsicI2cController.CompletionCycles);

        Assert.Equal(1, i2c.CompletedSteps);
        Assert.Equal(0xb0, cpu.ReadData(AsicI2cController.ControlAddress));
        Assert.Equal(0x08, i2c.Status);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
    }

    [Fact]
    public void ClockCompletionIsImmediatelyVisibleToCpuInterruptArbitration()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        _ = new AsicI2cController(cpu, clock, interrupts, new S4595Display());
        cpu.Data[0x5f] = 0x80;

        cpu.WriteData(AsicI2cController.ControlAddress, 0xa0);
        clock.AdvanceTo(AsicI2cController.CompletionCycles);

        Assert.Equal(0, cpu.Cycles);
        Assert.Equal(
            AsicInterruptController.HighPriorityVectorWord,
            cpu.Tick());
    }

    [Fact]
    public void BootConfigurationWaitsForACompleteNineClockTransfer()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var i2c = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            new S4595Display());

        cpu.WriteData(AsicI2cController.ConfigurationAddress, AsicI2cController.BootConfiguration);
        cpu.WriteData(AsicI2cController.ControlAddress, 0xa0);
        clock.AdvanceBy(32);

        Assert.Equal(AsicI2cController.BootConfiguration,
            cpu.ReadData(AsicI2cController.ConfigurationAddress));
        Assert.Equal(0, i2c.CompletedSteps);

        clock.AdvanceBy(AsicI2cController.CompletionCycles - 32);

        Assert.Equal(1, i2c.CompletedSteps);
        Assert.Equal(0x08, i2c.Status);
    }

    [Fact]
    public void UnknownPanelAddressReturnsWriteAddressNack()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var display = new S4595Display();
        var i2c = new AsicI2cController(cpu, clock, interrupts, display);

        StartTransfer(cpu, clock);
        WriteStep(cpu, clock, 0x70);

        Assert.Equal(0x20, i2c.Status);
        WriteStep(cpu, clock, 0x12);
        Assert.Equal(0x20, i2c.Status);
        Assert.Equal(0, display.TransactionCount);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
    }

    [Fact]
    public void ControlOnlyTerminalPhaseDoesNotRetransmitStaleData()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        byte[]? completedPayload = null;
        var i2c = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            AsicI2cController.DataAddress,
            AsicInterruptController.I2c1Source,
            _ => true,
            (_, payload) => completedPayload = payload);

        StartTransfer(cpu, clock);
        WriteStep(cpu, clock, S4595Display.WriteAddress);
        WriteStep(cpu, clock, S4595Display.ScanRowCommand);

        cpu.WriteData(AsicI2cController.ControlAddress, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
        cpu.WriteData(AsicI2cController.ControlAddress, 0x98);

        Assert.Equal(new byte[] { S4595Display.ScanRowCommand }, completedPayload);
        Assert.Equal(4, i2c.CompletedSteps);
        Assert.Equal(1, i2c.CompletedTransactions);
    }

    [Fact]
    public void PrimaryControllerUsesItsOwnRegisterBlockAndInterruptSource()
    {
        const int dataAddress = 0x0839;
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var i2c = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            dataAddress,
            AsicInterruptController.I2cSource);

        cpu.WriteData(dataAddress + 3, AsicI2cController.BootConfiguration);
        cpu.WriteData(dataAddress + 1, 0xa0);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);

        Assert.Equal(1, i2c.CompletedSteps);
        Assert.Equal(0x08, cpu.ReadData(dataAddress + 2));
        Assert.Equal(0xb0, cpu.ReadData(dataAddress + 1));
        Assert.Equal(AsicInterruptController.I2cSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
        Assert.Equal(0, cpu.ReadData(AsicI2cController.StatusAddress));
    }

    [Fact]
    public void RepeatedStartCommitsSelectorAndReadsFromAttachedDevice()
    {
        const int dataAddress = 0x0839;
        byte[]? selector = null;
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var i2c = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            dataAddress,
            AsicInterruptController.I2cSource,
            address => (address & 0xfe) == 0x90,
            (_, payload) => selector = payload,
            _ => 0x42);

        StartTransfer(cpu, clock, dataAddress);
        WriteStep(cpu, clock, dataAddress, 0x90);
        WriteStep(cpu, clock, dataAddress, 0xa1);
        StartTransfer(cpu, clock, dataAddress);

        Assert.Equal(new byte[] { 0xa1 }, selector);
        Assert.Equal(0x10, i2c.Status);

        WriteStep(cpu, clock, dataAddress, 0x91);
        cpu.WriteData(dataAddress + 1, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);

        Assert.Equal(0x58, i2c.Status);
        Assert.Equal(0x42, cpu.ReadData(dataAddress));

        cpu.WriteData(dataAddress + 1, 0x98);
        Assert.Equal(1, i2c.CompletedTransactions);
    }

    [Fact]
    public void DisplayTransmitDmaStreamsStagedExternalRamAfterScanRow()
    {
        const int source = AsicCommandPort.GraphicsRamBase + 0x0200;
        var cpu = new Cpu(new byte[0x100], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var display = new S4595Display();
        var i2c = new AsicI2cController(cpu, clock, interrupts, display);
        cpu.Data[source] = 0x21;
        cpu.Data[source + 1] = 0x43;
        cpu.Data[source + 2] = 0x65;
        cpu.Data[source + 3] = 0x87;

        StartTransfer(cpu, clock);
        WriteStep(cpu, clock, S4595Display.WriteAddress);
        WriteStep(cpu, clock, S4595Display.ScanRowCommand);
        StageDma(cpu, source, 4, AsicI2cController.DmaControlForward);
        cpu.WriteData(AsicI2cController.ConfigurationAddress,
            AsicI2cController.TransmitDmaConfiguration);
        var dmaCycle = clock.Cycles;
        var dmaPc = cpu.PC;
        cpu.WriteData(AsicI2cController.ControlAddress, 0x80);
        clock.AdvanceBy(AsicI2cController.GetDmaCompletionCycles(4) - 1);
        Assert.Equal(3, i2c.CompletedSteps);
        clock.AdvanceBy(1);
        var writeCycle = clock.Cycles;
        var writePc = cpu.PC;
        cpu.WriteData(AsicI2cController.ControlAddress, 0x98);

        Assert.Equal(4, i2c.CompletedSteps);
        Assert.Equal(1, i2c.DmaTransferCount);
        Assert.Equal(4, i2c.DmaByteCount);
        Assert.Equal(source, i2c.LastDmaSource);
        Assert.Equal(4, i2c.LastDmaLength);
        Assert.Equal(dmaCycle, i2c.LastDmaCycle);
        Assert.Equal(dmaPc, i2c.LastDmaPc);
        Assert.Equal(writeCycle, i2c.LastWriteCycle);
        Assert.Equal(writePc, i2c.LastWritePc);
        Assert.Equal(4, display.PixelWriteCount);
        Assert.Equal(0x21, display.GetPixel(0, 0));
        Assert.Equal(0x43, display.GetPixel(1, 0));
        Assert.Equal(0x65, display.GetPixel(2, 0));
        Assert.Equal(0x87, display.GetPixel(3, 0));
    }

    [Fact]
    public void DisplayTransmitDmaPostsOneBulkCompletionForDescriptorLength()
    {
        const int source = AsicCommandPort.GraphicsRamBase + 0x0200;
        const int length = 512;
        var cpu = new Cpu(new byte[0x100], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var display = new S4595Display();
        var i2c = new AsicI2cController(cpu, clock, interrupts, display);

        StartTransfer(cpu, clock);
        WriteStep(cpu, clock, S4595Display.WriteAddress);
        WriteStep(cpu, clock, S4595Display.ScanRowCommand);
        StageDma(cpu, source, length, AsicI2cController.DmaControlForward);
        cpu.WriteData(
            AsicI2cController.ConfigurationAddress,
            AsicI2cController.TransmitDmaConfiguration);
        cpu.WriteData(AsicI2cController.ControlAddress, 0x80);

        clock.AdvanceBy(AsicI2cController.GetDmaCompletionCycles(length) - 1);
        Assert.Equal(3, i2c.CompletedSteps);
        clock.AdvanceBy(1);

        Assert.Equal(4, i2c.CompletedSteps);
        Assert.Equal(1, i2c.DmaTransferCount);
        Assert.Equal(length, i2c.DmaByteCount);
        Assert.Equal(0x28, i2c.Status);
    }

    [Fact]
    public void DisplayTransmitDmaRejectsUnknownStagingControl()
    {
        const int source = AsicCommandPort.GraphicsRamBase + 0x0200;
        var cpu = new Cpu(new byte[0x100], 0x110000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var display = new S4595Display();
        var i2c = new AsicI2cController(cpu, clock, interrupts, display);

        StartTransfer(cpu, clock);
        WriteStep(cpu, clock, S4595Display.WriteAddress);
        WriteStep(cpu, clock, S4595Display.ScanRowCommand);
        StageDma(cpu, source, 1, 0x34);
        cpu.WriteData(AsicI2cController.ConfigurationAddress,
            AsicI2cController.TransmitDmaConfiguration);
        cpu.WriteData(AsicI2cController.ControlAddress, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);

        Assert.Equal(0x00, i2c.Status);
        Assert.Equal(1, i2c.RejectedDmaCount);
        Assert.Equal(0, i2c.DmaTransferCount);
        Assert.Equal(0, display.PixelWriteCount);
    }

    static void WriteStep(Cpu cpu, MiaSystemClock clock, byte value)
        => WriteStep(cpu, clock, AsicI2cController.DataAddress, value);

    static void WriteStep(
        Cpu cpu,
        MiaSystemClock clock,
        int dataAddress,
        byte value)
    {
        cpu.WriteData(dataAddress, value);
        cpu.WriteData(dataAddress + 1, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
    }

    static void StartTransfer(Cpu cpu, MiaSystemClock clock)
        => StartTransfer(cpu, clock, AsicI2cController.DataAddress);

    static void StartTransfer(Cpu cpu, MiaSystemClock clock, int dataAddress)
    {
        cpu.WriteData(dataAddress + 1, 0xa0);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
    }

    static void StageDma(Cpu cpu, int source, int length, byte control)
    {
        cpu.WriteData(AsicI2cController.DmaSourceLowAddress, (byte)source);
        cpu.WriteData(AsicI2cController.DmaSourceLowAddress + 1, (byte)(source >> 8));
        cpu.WriteData(AsicI2cController.DmaLengthLowAddress, (byte)length);
        cpu.WriteData(AsicI2cController.DmaLengthLowAddress + 1, (byte)(length >> 8));
        cpu.WriteData(AsicI2cController.DmaControlAddress, control);
    }
}
