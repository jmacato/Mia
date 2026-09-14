// SPDX-License-Identifier: MIT

using Arm7Core;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemBusTests
{
    [Fact]
    public void StalledSerialReceiversHaveBoundedBacklogsAndResumeInOrder()
    {
        var bus = CreateBus();
        bus.WriteByte(0x00800b14, 3, ArmAccess.None);
        for (int i = 0; i < 100000; i++)
        {
            bus.QueueUart1ReceivedByte((byte)i);
            bus.QueueInfraredReceivedByte((byte)i);
        }
        Assert.Equal(100000 - ArmModemBus.MaximumUartReceiveBacklog, bus.Uart1DroppedReceiveCount);
        Assert.Equal(255u, bus.ReadByte(0x00800b1c, ArmAccess.None));
        for (int i = 0; i < ArmModemBus.MaximumUartReceiveBacklog; i++)
            Assert.Equal((uint)(byte)i, bus.ReadByte(0x00800b10, ArmAccess.None));
        for (int i = 0; i < ArmModemBus.MaximumInfraredReceiveBacklog; i++)
            Assert.Equal((uint)(byte)i, bus.ReadByte(0x00800b30, ArmAccess.None));
        Assert.Equal(0u, bus.ReadByte(0x00800b3c, ArmAccess.None));
        bus.QueueExternalSerialReceivedBytes([0xab]);
        Assert.Equal(0xabu, bus.ReadByte(0x00800b30, ArmAccess.None));
    }

    [Fact]
    public void RepeatedlyCancelledTimersDoNotAccumulateBehindAnEarlierLiveTimer()
    {
        var bus = CreateBus();
        int calls = 0;
        using var first = bus.SchedulePeripheralEvent(100000, _ => calls++);
        for (int i = 0; i < 10000; i++)
        {
            using var cancelled = bus.SchedulePeripheralEvent(200000 + i, _ => calls += 100);
        }
        var field = typeof(ArmModemBus).GetField("_scheduledPeripheralEvents",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var queue = (PriorityQueue<ArmModemBusScheduledPeripheralEvent, (long, long)>)field.GetValue(bus)!;
        Assert.InRange(queue.Count, 1, 32);
        bus.IdleUntilCycle(220000);
        Assert.Equal(1, calls);
        Assert.Empty(queue.UnorderedItems);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(ArmModemBus.InternalRamSize - 4)]
    [InlineData(ArmModemBus.ExternalRamBase)]
    [InlineData(ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamSize - 4)]
    [InlineData(ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan - 4)]
    public void CodeFetchesReadRamAndMirrorsAtTheSameBusCycle(uint address)
    {
        var bus = CreateBus();
        bus.WriteWord(address, 0x78563412, ArmAccess.None);
        long start = bus.Cycles;
        Assert.Equal(0x3412u, bus.ReadHalf(address, ArmAccess.Code));
        Assert.Equal(0x7856u, bus.ReadHalf(address + 2, ArmAccess.Code));
        Assert.Equal(0x78563412u, bus.ReadWord(address, ArmAccess.Code));
        Assert.Equal(start + 3, bus.Cycles);
        bus.WriteByte(address + 3, 0xab, ArmAccess.None);
        bus.WriteHalf(address, 0xcdef, ArmAccess.None);
        Assert.Equal(0xabu, bus.ReadByte(address + 3, ArmAccess.None));
        Assert.Equal(0xcdefu, bus.ReadHalf(address, ArmAccess.None));
        Assert.Equal(0xab56cdefu, bus.ReadWord(address, ArmAccess.None));
        Assert.Equal(start + 8, bus.Cycles);
    }

    [Fact]
    public void RamStoresPreserveObserverAddressesValuesAndPeripheralDeadlines()
    {
        var bus = CreateBus();
        bus.CurrentPc = 0x01001000;
        var writes = new List<ArmModemRamWrite>();
        var mmio = new List<ArmModemMmioAccess>();
        var deadlines = new List<long>();
        bus.ExternalRamWritten += writes.Add;
        bus.MmioAccessed += mmio.Add;
        bus.SchedulePeripheralEvent(2, deadlines.Add);
        uint address = ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamSize;
        bus.WriteByte(address, 0x12, ArmAccess.None);
        bus.WriteHalf(address + 2, 0x3456, ArmAccess.None);
        bus.WriteWord(address + 4, 0x789abcde, ArmAccess.None);

        Assert.Equal([2L], deadlines);
        Assert.Equal([1L, 2L, 3L], writes.Select(w => w.Cycle));
        Assert.Equal([address, address + 2, address + 4], writes.Select(w => w.Address));
        Assert.Equal([1, 2, 4], writes.Select(w => w.Size));
        Assert.Equal([0x12u, 0x3456u, 0x789abcdeu], writes.Select(w => w.Value));
        Assert.Empty(mmio);
        Assert.Equal(0x12u, bus.ReadByte(ArmModemBus.ExternalRamBase, ArmAccess.None));
        Assert.Equal(0x3456u, bus.ReadHalf(ArmModemBus.ExternalRamBase + 2, ArmAccess.None));
        Assert.Equal(0x789abcdeu, bus.ReadWord(ArmModemBus.ExternalRamBase + 4, ArmAccess.None));
    }

    [Fact]
    public void ScheduledExternalDspFrameIsDeliveredWhileTheModemBusIsIdle()
    {
        var bus = CreateBus();
        long? deliveredAt = null;
        bus.ExternalDspFrameDue += cycle => deliveredAt = cycle;

        bus.ScheduleExternalDspFrame(37);
        bus.IdleUntilCycle(100);

        Assert.Equal(37, deliveredAt);
        Assert.Equal(100, bus.Cycles);
    }

    [Fact]
    public void DspInboundTransferByteCountTracksTheNativeFifo()
    {
        var bus = CreateBus();

        bus.QueueDspInboundTransfer(0, 3, [0x0a]);

        Assert.Equal(3, bus.DspInboundTransferByteCount);
        Assert.Equal(0u, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x0bu, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x0au, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0, bus.DspInboundTransferByteCount);
    }

    [Fact]
    public void UartDropsPreconfigurationBytesAndExposesEnabledReceiveFifo()
    {
        var bus = CreateBus();
        bus.QueueUart1ReceivedByte(0xab);

        Assert.Equal(1, bus.Uart1DroppedReceiveCount);
        Assert.Equal(0u, bus.ReadByte(0x00800b1c, ArmAccess.None));

        bus.WriteByte(0x00800b14, 3, ArmAccess.None);
        bus.QueueUart1ReceivedByte(0x4c);
        bus.QueueUart1ReceivedByte(0x41);

        Assert.True(bus.Uart1ReceiveEnabled);
        Assert.Equal(2u, bus.ReadByte(0x00800b1c, ArmAccess.None));
        Assert.Equal(0x4cu, bus.ReadByte(0x00800b10, ArmAccess.None));
        Assert.Equal(0x41u, bus.ReadByte(0x00800b10, ArmAccess.None));
    }

    [Fact]
    public void UartReceiveControlFlushesStaleFifoBeforeSyncAttempt()
    {
        var bus = CreateBus();
        bus.WriteByte(0x00800b14, 3, ArmAccess.None);
        bus.QueueUart1ReceivedByte(0x00);

        bus.WriteByte(0x00800b14, 1, ArmAccess.None);

        Assert.Equal(0u, bus.ReadByte(0x00800b1c, ArmAccess.None));
    }

    [Fact]
    public void UartTransmitOccursOnlyThroughFifoMmio()
    {
        var bus = CreateBus();
        var transmitted = new List<byte>();
        bus.Uart1ByteTransmitted += transmitted.Add;

        bus.WriteByte(0x00800b00, 0x4c, ArmAccess.None);
        bus.WriteByte(0x00800b00, 0x31, ArmAccess.None);

        Assert.Empty(transmitted);
        Assert.Equal(2, bus.Uart1TransmitCount);
        Assert.Equal(2u, bus.ReadByte(0x00800b0c, ArmAccess.None));

        for (var cycle = 0; cycle < ArmModemBus.Uart1CharacterCycles * 2; cycle++)
        {
            bus.Idle();
        }

        Assert.Equal([0x4c, 0x31], transmitted);
        Assert.Equal(0u, bus.ReadByte(0x00800b0c, ArmAccess.None));
    }

    [Fact]
    public void UartTransmitPublishesCompletionWhenSerializationStarts()
    {
        var bus = CreateBus();
        var scheduled = new List<(byte Value, long Start, long Completion)>();
        bus.Uart1TransmissionScheduled += (value, completion) =>
            scheduled.Add((value, bus.Cycles, completion));

        bus.WriteByte(0x00800b00, 0x4c, ArmAccess.None);
        bus.WriteByte(0x00800b00, 0x31, ArmAccess.None);

        Assert.Equal(2, scheduled.Count);
        Assert.Equal((byte)0x4c, scheduled[0].Value);
        Assert.Equal(
            scheduled[0].Start + ArmModemBus.Uart1CharacterCycles,
            scheduled[0].Completion);
        Assert.Equal((byte)0x31, scheduled[1].Value);
        Assert.Equal(
            scheduled[0].Completion + ArmModemBus.Uart1CharacterCycles,
            scheduled[1].Completion);
    }

    [Fact]
    public void ScheduledUartReceiveEvaluatesEnableAtArrivalCycle()
    {
        var bus = CreateBus();
        bus.ScheduleUart1ReceivedByte(0x41, 20);
        bus.IdleUntilCycle(10);
        bus.WriteByte(0x00800b14, 3, ArmAccess.None);

        bus.IdleUntilCycle(19);
        Assert.Equal(0, bus.Uart1ReceivedCount);

        bus.IdleUntilCycle(20);

        Assert.Equal(1, bus.Uart1ReceivedCount);
        Assert.Equal(0x41u, bus.ReadByte(0x00800b10, ArmAccess.None));
    }

    [Fact]
    public void ScheduledUartReceiveFailsClosedWhenInstalledLate()
    {
        var bus = CreateBus();
        bus.IdleUntilCycle(20);

        var error = Assert.Throws<InvalidOperationException>(
            () => bus.ScheduleUart1ReceivedByte(0x41, 19));

        Assert.Contains("behind modem cycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownRegisterAccessFailsClosedWithFirmwarePc()
    {
        var bus = CreateBus();
        bus.CurrentPc = 0x01001234;

        ArmModemBusAccessException error = Assert.Throws<ArmModemBusAccessException>(
            () => bus.ReadByte(0x0080ffff, ArmAccess.None));

        Assert.Contains("pc=0x01001234", error.Message, StringComparison.Ordinal);
        Assert.Contains("address=0x0080ffff", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareProgrammedTimerRaisesAndAcknowledgesUnmaskedIrq()
    {
        var bus = CreateBus();
        bus.WriteWord(0x00800508, uint.MaxValue, ArmAccess.None);
        bus.WriteByte(0x00800914, 0xcb, ArmAccess.None);
        bus.WriteByte(0x00800910, 1, ArmAccess.None);
        bus.WriteByte(0x00800708, 4, ArmAccess.None);
        bus.WriteByte(0x00800710, 0x2d, ArmAccess.None);
        bus.WriteByte(0x00800718, 0x0b, ArmAccess.None);

        Assert.Equal(
            ArmModemBus.OseTimerInterruptPeriodCycles,
            bus.TimerInterruptPeriodCycles);
        for (long cycle = 0; cycle < bus.TimerInterruptPeriodCycles; cycle++)
        {
            bus.Idle();
        }

        Assert.Equal(ArmModemBus.TimerInterruptBit, bus.IrqPending);
        Assert.False(bus.IrqLineAsserted);

        bus.WriteWord(
            0x0080050c,
            ArmModemBus.TimerInterruptBit,
            ArmAccess.None);

        Assert.True(bus.IrqLineAsserted);
        Assert.Equal(
            ArmModemBus.TimerInterruptBit,
            bus.ReadWord(0x00800504, ArmAccess.None));
        Assert.Equal(0x2du, bus.ReadByte(0x00800710, ArmAccess.None));
        Assert.False(bus.IrqLineAsserted);
    }

    [Fact]
    public void InterruptStatusSplitsServiceableAndRawSources()
    {
        var bus = CreateBus();
        bus.WriteByte(0x00800b14, 3, ArmAccess.None);

        Assert.NotEqual(0u, bus.IrqPending & ArmModemBus.Uart1TransmitInterruptBit);
        Assert.Equal(0u, bus.ReadWord(0x00800500, ArmAccess.None));
        Assert.Equal(
            ArmModemBus.Uart1TransmitInterruptBit,
            bus.ReadWord(0x00800504, ArmAccess.None));

        bus.WriteWord(
            0x0080050c,
            ArmModemBus.Uart1TransmitInterruptBit,
            ArmAccess.None);

        Assert.Equal(
            ArmModemBus.Uart1TransmitInterruptBit,
            bus.ReadWord(0x00800500, ArmAccess.None));
        Assert.Equal(
            ArmModemBus.Uart1TransmitInterruptBit,
            bus.ReadWord(0x00800504, ArmAccess.None));
    }

    [Fact]
    public void UartEmptyAndReceiveReadyAreMaskedLevelInterrupts()
    {
        var bus = CreateBus();
        bus.WriteByte(0x00800b14, 3, ArmAccess.None);

        Assert.Equal(
            ArmModemBus.Uart1TransmitInterruptBit,
            bus.IrqPending & ArmModemBus.Uart1TransmitInterruptBit);
        Assert.False(bus.IrqLineAsserted);

        bus.WriteWord(
            0x0080050c,
            ArmModemBus.Uart1TransmitInterruptBit,
            ArmAccess.None);
        Assert.True(bus.IrqLineAsserted);

        bus.WriteWord(
            0x00800508,
            ArmModemBus.Uart1TransmitInterruptBit,
            ArmAccess.None);
        bus.QueueUart1ReceivedByte(0x41);
        bus.WriteWord(
            0x0080050c,
            ArmModemBus.Uart1ReceiveInterruptBit,
            ArmAccess.None);

        Assert.True(bus.IrqLineAsserted);
        Assert.Equal(
            ArmModemBus.Uart1ReceiveInterruptBit,
            bus.ReadWord(0x00800504, ArmAccess.None) &
                ArmModemBus.Uart1ReceiveInterruptBit);
        Assert.Equal(0x41u, bus.ReadByte(0x00800b10, ArmAccess.None));
        Assert.False(bus.IrqLineAsserted);
    }

    [Fact]
    public void FullUartTransmitFifoDeassertsReadyUntilACharacterDrains()
    {
        var bus = CreateBus();
        bus.WriteByte(0x00800b14, 3, ArmAccess.None);
        bus.WriteWord(
            0x0080050c,
            ArmModemBus.Uart1TransmitInterruptBit,
            ArmAccess.None);
        for (var index = 0; index < ArmModemBus.Uart1TransmitFifoCapacity; index++)
        {
            bus.WriteByte(0x00800b00, (byte)index, ArmAccess.None);
        }

        Assert.False(bus.IrqLineAsserted);

        for (var cycle = 0; cycle < ArmModemBus.Uart1CharacterCycles; cycle++)
        {
            bus.Idle();
        }

        Assert.True(bus.IrqLineAsserted);
        Assert.Equal(ArmModemBus.Uart1TransmitFifoCapacity - 1,
            bus.Uart1TransmitQueuedCount);
    }

    [Fact]
    public void DebugUartReportsIdleAndTransmitsThroughItsDataRegister()
    {
        var bus = CreateBus();
        var transmitted = new List<byte>();
        bus.Uart2ByteTransmitted += transmitted.Add;

        Assert.Equal(0x60u, bus.ReadByte(0x00800214, ArmAccess.None));
        bus.WriteByte(0x00800200, (byte)'O', ArmAccess.None);
        bus.WriteByte(0x00800200, (byte)'K', ArmAccess.None);

        Assert.Equal([(byte)'O', (byte)'K'], transmitted);
        Assert.Equal(2, bus.Uart2TransmitCount);
    }

    [Fact]
    public void InfraredFifoExposesNativeSirTransmitAndReceiveContract()
    {
        var bus = CreateBus();
        var transmitted = new List<byte>();
        bus.InfraredByteTransmitted += transmitted.Add;

        Assert.Equal(0x60u, bus.ReadByte(0x00800114, ArmAccess.None));
        Assert.Equal(0u, bus.ReadByte(0x00800b2c, ArmAccess.None));
        Assert.Equal(0u, bus.ReadByte(0x00800b3c, ArmAccess.None));

        bus.WriteByte(0x00800b20, 0xc0, ArmAccess.None);
        bus.WriteByte(0x00800b20, 0x01, ArmAccess.None);
        Assert.Equal([0xc0, 0x01], transmitted);
        Assert.Equal(2, bus.InfraredTransmitCount);

        bus.WriteWord(
            0x0080050c,
            ArmModemBus.InfraredReceiveInterruptBit,
            ArmAccess.None);
        bus.QueueInfraredReceivedBytes([0xc0, 0x42, 0xc1]);

        Assert.True(bus.IrqLineAsserted);
        Assert.Equal(3u, bus.ReadByte(0x00800b3c, ArmAccess.None));
        Assert.Equal(0xc0u, bus.ReadByte(0x00800b30, ArmAccess.None));
        Assert.Equal(0x42u, bus.ReadByte(0x00800b30, ArmAccess.None));
        Assert.Equal(0xc1u, bus.ReadByte(0x00800b30, ArmAccess.None));
        Assert.False(bus.IrqLineAsserted);
    }

    [Fact]
    public void FifoThreeRoutesNormalCableBytesSeparatelyFromInfrared()
    {
        var bus = CreateBus();
        var infrared = new List<byte>();
        var serial = new List<byte>();
        bus.InfraredByteTransmitted += infrared.Add;
        bus.ExternalSerialByteTransmitted += serial.Add;

        bus.WriteByte(0x00800104, 0x04, ArmAccess.None);
        bus.WriteByte(0x00800b20, 0xc0, ArmAccess.None);
        bus.WriteByte(0x00800104, 0x0c, ArmAccess.None);
        bus.WriteByte(0x00800b20, (byte)'A', ArmAccess.None);

        Assert.Equal([0xc0], infrared);
        Assert.Equal([(byte)'A'], serial);
        Assert.True(bus.ExternalSerialPortEnabled);
    }

    [Fact]
    public void NormalCableGpioRaisesRecoveredAttachAndDetachInterrupt()
    {
        var bus = CreateBus();
        bus.WriteWord(
            0x0080050c,
            ArmModemBus.NormalCableDetectInterruptBit,
            ArmAccess.None);

        bus.SetNormalCableConnected(true);
        Assert.Equal(0x80u, bus.ReadByte(0x00800400, ArmAccess.None) & 0x80);
        Assert.NotEqual(0u, bus.IrqPending & ArmModemBus.NormalCableDetectInterruptBit);
        bus.WriteByte(0x00800954, 0, ArmAccess.None);
        Assert.Equal(0u, bus.IrqPending & ArmModemBus.NormalCableDetectInterruptBit);

        bus.SetNormalCableConnected(false);
        Assert.Equal(0u, bus.ReadByte(0x00800400, ArmAccess.None) & 0x80);
        Assert.NotEqual(0u, bus.IrqPending & ArmModemBus.NormalCableDetectInterruptBit);
    }

    [Fact]
    public void RecoveredTenMicrosecondOneShotRaisesAndAcknowledgesItsInterrupt()
    {
        var bus = CreateBus();
        bus.WriteWord(
            0x0080050c,
            ArmModemBus.OneShotInterruptBit,
            ArmAccess.None);
        bus.WriteByte(0x00800930, 1, ArmAccess.None);
        bus.WriteHalf(0x0080071c, 2, ArmAccess.None);
        long programmedCycle = bus.Cycles;

        bus.IdleUntilCycle(programmedCycle + 259);
        Assert.Equal(0u, bus.IrqPending & ArmModemBus.OneShotInterruptBit);
        bus.IdleUntilCycle(programmedCycle + 260);
        Assert.NotEqual(0u, bus.IrqPending & ArmModemBus.OneShotInterruptBit);

        bus.WriteByte(0x00800930, 0, ArmAccess.None);
        Assert.Equal(0u, bus.IrqPending & ArmModemBus.OneShotInterruptBit);
    }

    [Fact]
    public void InfraredReceiveControlFlushesStaleFifo()
    {
        var bus = CreateBus();
        bus.QueueInfraredReceivedBytes([0xc0, 0x42, 0xc1]);

        bus.WriteByte(0x00800b34, 1, ArmAccess.None);

        Assert.Equal(0u, bus.ReadByte(0x00800b3c, ArmAccess.None));
    }

    [Fact]
    public void DspInboundPayloadUsesVariableTransferFifoAndIrq()
    {
        var bus = CreateBus();
        bus.WriteWord(
            0x0080050c,
            ArmModemBus.DspInterruptBit,
            ArmAccess.None);

        bus.QueueDspInboundPayload([0x0a, 0x30, 0x2f]);

        Assert.True(bus.IrqLineAsserted);
        Assert.Equal(
            ArmModemBus.DspInterruptBit,
            bus.IrqPending & ArmModemBus.DspInterruptBit);
        Assert.Equal(0x10u, bus.ReadByte(0x00800800, ArmAccess.None));
        Assert.Equal(0u, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x1bu, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x0au, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x30u, bus.ReadByte(0x00800810, ArmAccess.None));
        Assert.Equal(0x2fu, bus.ReadByte(0x00800810, ArmAccess.None));

        bus.WriteByte(0x00800800, 0x10, ArmAccess.None);

        Assert.False(bus.IrqLineAsserted);
        Assert.Equal(0u, bus.IrqPending & ArmModemBus.DspInterruptBit);
        Assert.Throws<ArmModemBusAccessException>(
            () => bus.ReadByte(0x00800810, ArmAccess.None));
    }

    [Fact]
    public void DspControlWriteAcknowledgesTheCurrentEncodedStatus()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);

        bus.AssertDspStatus(0x15);

        Assert.Equal(0x15u, bus.ReadByte(0x00800800, ArmAccess.None));

        // The write is a control/acknowledgement mask, not a bit-for-bit W1C
        // reflection of the encoded read value.
        bus.WriteByte(0x00800800, 0x11, ArmAccess.None);

        Assert.Equal(0u, bus.ReadByte(0x00800800, ArmAccess.None));
        Assert.Equal(0u, bus.IrqPending & ArmModemBus.DspInterruptBit);
    }

    [Fact]
    public void DspIndexedReadsAutoIncrementAndFailClosedWhenUnset()
    {
        var bus = CreateBus();
        bus.SetDspIndexedRegister(0x0e, 0x20);
        bus.SetDspIndexedRegister(0x0f, 0xd0);

        bus.WriteByte(0x00800808, 0x0e, ArmAccess.None);

        Assert.Equal(0x20u, bus.ReadByte(0x00800808, ArmAccess.None));
        Assert.Equal(0xd0u, bus.ReadByte(0x00800808, ArmAccess.None));
        Assert.Throws<ArmModemBusAccessException>(
            () => bus.ReadByte(0x00800808, ArmAccess.None));
    }

    [Fact]
    public void DspPacketPortPublishesLengthDelimitedPackets()
    {
        var bus = CreateBus();
        var packets = new List<ArmModemDspPacket>();
        bus.DspPacketTransmitted += packets.Add;

        foreach (byte value in new byte[] { 0x45, 0x03, 0x11, 0x22, 0x33 })
        {
            bus.WriteByte(0x00800804, value, ArmAccess.None);
        }

        ArmModemDspPacket packet = Assert.Single(packets);
        Assert.Equal(0x45, packet.Type);
        Assert.Equal([0x11, 0x22, 0x33], packet.Payload);
    }

    [Fact]
    public void DspSecondaryPortsPublishFramedTransfers()
    {
        var bus = CreateBus();
        var transfers = new List<ArmModemDspTransfer>();
        bus.DspTransferTransmitted += transfers.Add;

        bus.WriteByte(0x0080080c, 0x00, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x05, ArmAccess.None);
        bus.WriteByte(0x0080080c, 0x03, ArmAccess.None);
        bus.WriteByte(0x0080080c, 0x00, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x1f, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x0c, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x30, ArmAccess.None);
        bus.WriteByte(0x00800810, 0x2f, ArmAccess.None);
        bus.WriteByte(0x0080080c, 0x03, ArmAccess.None);

        Assert.Collection(
            transfers,
            setup =>
            {
                Assert.Equal(0x00, setup.Control);
                Assert.Equal((byte?)5, setup.Kind);
                Assert.Equal(0x03, setup.Trailer);
                Assert.Empty(setup.Payload);
            },
            payload =>
            {
                Assert.Equal(0x00, payload.Control);
                Assert.Equal((byte?)7, payload.Kind);
                Assert.Equal(0x03, payload.Trailer);
                Assert.Equal([0x0c, 0x30, 0x2f], payload.Payload);
            });
    }

    static ArmModemBus CreateBus() =>
        new([], ArmModemFlashProfile.StM36Dr216C);
}
