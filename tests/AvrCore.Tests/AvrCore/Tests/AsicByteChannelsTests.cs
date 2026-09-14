// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicByteChannelsTests
{
    [Fact]
    public void ReceiveOverrunRetainsUnreadBytesWithoutGrowingForever()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        for (int i = 0; i < 100000; i++) channels.QueueReceivedByte(1, (byte)i);
        for (int i = 0; i < AsicByteChannels.MaximumReceiveBacklog; i++)
            Assert.Equal((byte)i, cpu.ReadData(0x0915));
        Assert.Equal(0, cpu.ReadData(0x0916));
        channels.QueueReceivedByte(1, 0xab);
        Assert.Equal(0xab, cpu.ReadData(0x0915));
    }

    [Theory]
    [InlineData(0x0904)]
    [InlineData(0x0914)]
    public void StatusReportsTransmitterReadyAndPreservesOtherBits(int address)
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        cpu.Data[address] = 0x80;
        _ = new AsicByteChannels(cpu, new MiaSystemClock());

        Assert.Equal(0x82, cpu.ReadData(address));
    }

    [Theory]
    [InlineData(0x0903)]
    [InlineData(0x0913)]
    public void CompletionStatusReportsTransmitterIdle(int address)
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        _ = new AsicByteChannels(cpu, new MiaSystemClock());

        Assert.Equal(0x40, cpu.ReadData(address));
    }

    [Fact]
    public void TransmitWritesRemainVisibleAndAreObservable()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        var observedChannel = -1;
        byte observedValue = 0;
        channels.ByteTransmitted += (channel, value) => (observedChannel, observedValue) = (channel, value);

        cpu.WriteData(0x0915, 0xa5);

        Assert.Equal(1, observedChannel);
        Assert.Equal(0xa5, observedValue);
        Assert.Equal(0xa5, cpu.Data[0x0915]);
        Assert.Equal(1, channels.GetTransmitCount(1));
    }

    [Fact]
    public void TransmitSchedulePublishesSerializedArrivalCycles()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var channels = new AsicByteChannels(cpu, clock);
        var scheduled = new List<(byte Value, long Cycle)>();
        channels.TransmissionScheduled += (channel, value, cycle) =>
        {
            Assert.Equal(1, channel);
            scheduled.Add((value, cycle));
        };

        cpu.WriteData(0x0915, 0x41);
        cpu.WriteData(0x0915, 0x42);
        clock.AdvanceTo(1_000);
        cpu.WriteData(0x0915, 0x43);

        Assert.Equal(
            [
                (0x41, (long)AsicByteChannels.LinkTransmitCharacterCycles),
                (0x42, (long)AsicByteChannels.LinkTransmitCharacterCycles * 2),
                (0x43, 1_000L + AsicByteChannels.LinkTransmitCharacterCycles),
            ],
            scheduled);
    }

    [Fact]
    public void PostedTransmitWritesRetainCyclesAndOwnerOrder()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        using var worker = new MiaWorker("test byte-channel owner");
        var channels = new AsicByteChannels(
            cpu,
            clock,
            worker: worker);
        MiaMmioWorkerBinding.BindRange(cpu, worker, 0x0900, 0x091f);
        channels.EnablePostedTransmitWrites();
        var scheduled = new List<(int Thread, byte Value, long Cycle)>();
        channels.TransmissionScheduled += (_, value, cycle) =>
            scheduled.Add((Environment.CurrentManagedThreadId, value, cycle));

        cpu.WriteData(0x0915, 0x41);
        clock.AdvanceTo(100);
        cpu.WriteData(0x0915, 0x42);

        Assert.Equal(0x42, cpu.Data[0x0915]);
        channels.FlushPostedTransmitWrites();

        Assert.Equal(
            [
                (worker.ThreadId, (byte)0x41,
                    (long)AsicByteChannels.LinkTransmitCharacterCycles),
                (worker.ThreadId, (byte)0x42,
                    (long)AsicByteChannels.LinkTransmitCharacterCycles * 2),
            ],
            scheduled);
        Assert.Equal(2, channels.GetTransmitCount(1));
    }

    [Fact]
    public void TransmitOccupancyReadIgnoresGenericResetWrites()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        _ = new AsicByteChannels(cpu, new MiaSystemClock());

        cpu.WriteData(0x0917, 0xff);

        Assert.Equal(0, cpu.ReadData(0x0917));
        Assert.Equal(0xff, cpu.Data[0x0917]);
    }

    [Fact]
    public void IncomingFifoIsVisibleBeforeWindowAndIsConsumedInOrder()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        var received = new List<byte>();
        channels.ByteReceived += (channel, value) =>
        {
            Assert.Equal(1, channel);
            received.Add(value);
        };

        channels.QueueReceivedByte(1, 0x12);
        channels.QueueReceivedByte(1, 0x34);

        Assert.Equal(0x42, cpu.ReadData(0x0913));
        Assert.Equal(2, cpu.ReadData(0x0916));

        cpu.WriteData(0x091a, 2);

        Assert.Equal(0x42, cpu.ReadData(0x0913));
        Assert.Equal(2, cpu.ReadData(0x0916));
        Assert.Equal(0x12, cpu.ReadData(0x0915));
        Assert.Equal(1, cpu.ReadData(0x0916));
        Assert.Equal(0x42, cpu.ReadData(0x0913));
        Assert.Equal(0x34, cpu.ReadData(0x0915));
        Assert.Equal(0x40, cpu.ReadData(0x0913));
        Assert.Equal([0x12, 0x34], received);
        Assert.Equal(0, channels.GetReceiveQueueLength(1));
        Assert.Equal(0, channels.GetReceiveWindowRemaining(1));
        Assert.Equal(2, channels.GetReceiveCount(1));
    }

    [Fact]
    public void ReceiveWindowCountsAlreadyQueuedBytesWithoutHidingThem()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        channels.QueueReceivedByte(1, 0x12);
        channels.QueueReceivedByte(1, 0x34);
        channels.QueueReceivedByte(1, 0x56);

        cpu.WriteData(0x091a, 2);

        Assert.Equal(3, channels.GetReceiveQueueLength(1));
        Assert.Equal(0, channels.GetReceiveWindowRemaining(1));
    }

    [Fact]
    public void EnabledChannelOneWindowRaisesFirmwareLinkReceiveSource()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var channels = new AsicByteChannels(cpu, clock, interrupts);
        cpu.WriteData(0x0918, 0x40);
        cpu.WriteData(0x091a, 2);

        channels.QueueReceivedByte(1, 0x12);
        Assert.Equal(-1, cpu.NextInterrupt);

        channels.QueueReceivedByte(1, 0x34);

        Assert.Equal(AsicInterruptController.LinkReceiveSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
        Assert.Equal(1, channels.GetReceiveInterruptCount(1));
    }

    [Fact]
    public void SatisfiedNextReceiveWindowWaitsForFirmwareAcknowledge()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var channels = new AsicByteChannels(cpu, clock, interrupts);
        cpu.WriteData(0x0918, 0x40);
        cpu.WriteData(0x091a, 1);
        channels.QueueReceivedByte(1, 0x12);

        cpu.WriteData(0x091a, 1);
        channels.QueueReceivedByte(1, 0x34);

        Assert.Equal(1, channels.GetReceiveInterruptCount(1));

        cpu.WriteData(0x0911, 0x40);

        Assert.Equal(2, channels.GetReceiveInterruptCount(1));
    }

    [Fact]
    public void ChannelOneTransmitReadyMaskRaisesFirmwareLinkTransmitSource()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var channels = new AsicByteChannels(cpu, clock, interrupts);

        cpu.WriteData(0x0919, 0x02);

        Assert.Equal(-1, cpu.NextInterrupt);

        clock.AdvanceBy(AsicByteChannels.LinkTransmitCharacterCycles);

        Assert.Equal(AsicInterruptController.LinkTransmitSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
        Assert.Equal(1, channels.GetTransmitInterruptCount(1));
    }

    [Fact]
    public void ChannelOneTransmitReadyMaskDoesNotRaiseWhileDisabled()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var channels = new AsicByteChannels(cpu, clock, interrupts);

        cpu.WriteData(0x0919, 0x00);

        Assert.Equal(-1, cpu.NextInterrupt);
        Assert.Equal(0, channels.GetTransmitInterruptCount(1));
    }

    [Fact]
    public void TransmitReadyInterruptWaitsForTheWrittenBurstToDrain()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var channels = new AsicByteChannels(cpu, clock, interrupts);
        cpu.WriteData(0x0915, 0xab);
        cpu.WriteData(0x0915, 0xba);

        cpu.WriteData(0x0919, 0x02);
        clock.AdvanceBy(AsicByteChannels.LinkTransmitCharacterCycles);

        Assert.Equal(-1, cpu.NextInterrupt);

        clock.AdvanceBy(AsicByteChannels.LinkTransmitCharacterCycles);

        Assert.Equal(AsicInterruptController.HighPriorityVectorWord, cpu.NextInterrupt);
    }
}
