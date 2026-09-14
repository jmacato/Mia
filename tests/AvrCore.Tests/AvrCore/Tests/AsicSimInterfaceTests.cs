// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicSimInterfaceTests
{
    [Fact]
    public void ReceiveOverrunRetainsTheBufferedResponseAndAllowsTheNextResponse()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var sim = new AsicSimInterface(cpu, new MiaSystemClock(), new AsicInterruptController(cpu));
        for (int i = 0; i < 100000; i++) sim.QueueReceivedByte((byte)i);
        Assert.Equal(AsicSimInterface.MaximumReceiveBacklog, sim.QueuedByteCount);
        for (int i = 0; i < AsicSimInterface.MaximumReceiveBacklog; i++)
            Assert.Equal((byte)i, cpu.ReadData(AsicSimInterface.DataAddress));
        sim.QueueReceivedByte(0xab);
        Assert.Equal(0xab, cpu.ReadData(AsicSimInterface.DataAddress));
        Assert.Equal(0, sim.QueuedByteCount);
    }

    [Fact]
    public void ReportsQueuedCountAndConsumesBytesThroughMmio()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(cpu, clock, interrupts);
        cpu.Data[AsicSimInterface.StatusAddress] = 0x05;
        sim.QueueReceivedByte(0x12);
        sim.QueueReceivedByte(0x34);

        Assert.Equal(0x15, cpu.ReadData(AsicSimInterface.StatusAddress));
        Assert.Equal(0x12, cpu.ReadData(AsicSimInterface.DataAddress));
        Assert.Equal(0x0d, cpu.ReadData(AsicSimInterface.StatusAddress));
        Assert.Equal(0x34, cpu.ReadData(AsicSimInterface.DataAddress));
        Assert.Equal(0x05, cpu.ReadData(AsicSimInterface.StatusAddress));
        Assert.Equal(2, sim.ReceivedByteCount);
        Assert.Equal(0, sim.QueuedByteCount);
    }

    [Fact]
    public void EmptyToNonemptyTransitionRaisesSimRxLowInterrupt()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(cpu, clock, interrupts);

        sim.QueueReceivedByte(0x12);
        sim.QueueReceivedByte(0x34);

        Assert.Equal(AsicInterruptController.SimRxLowSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
        Assert.Equal(1, interrupts.RaisedCount);
    }

    [Fact]
    public void SaturatesTheFiveBitReportedCount()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(cpu, clock, interrupts);
        for (var value = 0; value < 40; value++)
        {
            sim.QueueReceivedByte((byte)value);
        }

        Assert.Equal(0xf8, cpu.ReadData(AsicSimInterface.StatusAddress));
    }

    [Fact]
    public void DataWriteCompletesThroughSimTransmitInterrupt()
    {
        const int completionCycles = 7;
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(cpu, clock, interrupts, completionCycles);
        var transmitted = new List<byte>();
        sim.ByteTransmitted += transmitted.Add;

        cpu.WriteData(AsicSimInterface.DataAddress, 0xa0);
        clock.AdvanceBy(completionCycles - 1);

        Assert.Empty(transmitted);
        Assert.Equal(0, sim.TransmittedByteCount);

        clock.AdvanceBy(1);

        Assert.Equal(new byte[] { 0xa0 }, transmitted);
        Assert.Equal(1, sim.TransmittedByteCount);
        Assert.Equal(AsicInterruptController.SimTransmitSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
    }

    [Fact]
    public void ControlWritesExposeMaskedFirmwareVisibleTransitions()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var sim = new AsicSimInterface(cpu, clock, interrupts);
        var transitions = new List<(byte OldValue, byte NewValue)>();
        sim.ControlChanged += (oldValue, newValue) =>
            transitions.Add((oldValue, newValue));

        cpu.WriteData(AsicSimInterface.ControlAddress, 0x10);
        cpu.WriteData(AsicSimInterface.ControlAddress, 0x14, 0x04);

        Assert.Equal(0x14, sim.Control);
        Assert.Equal(0x14, cpu.ReadData(AsicSimInterface.ControlAddress));
        Assert.Equal(
            new List<(byte, byte)> { (0x00, 0x10), (0x10, 0x14) },
            transitions);
    }
}
