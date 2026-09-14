// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Arm7Core;
using Mia.Emulator.Diagnostics;
using Xunit;

namespace AvrCore.Tests;

public sealed class AccessTracePeripheralTests
{
    [Fact]
    public void AccessTracePeripheralDecodesPrimaryI2cWritesAndRepeatedReads()
    {
        AccessTraceAccumulator accumulator = CreateAccumulator(0, 100);
        var decoder = new PrimaryI2cTraceDecoder(accumulator.RecordPrimaryI2c);

        Write(decoder, 1, PrimaryI2cTraceDecoder.ControlAddress, 0xa0);
        Write(decoder, 2, PrimaryI2cTraceDecoder.DataAddress, 0x92);
        Write(decoder, 3, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 4, PrimaryI2cTraceDecoder.DataAddress, 0x84);
        Write(decoder, 5, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 6, PrimaryI2cTraceDecoder.DataAddress, 0x40);
        Write(decoder, 7, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 8, PrimaryI2cTraceDecoder.ControlAddress, 0x98);

        Write(decoder, 10, PrimaryI2cTraceDecoder.ControlAddress, 0xa0);
        Write(decoder, 11, PrimaryI2cTraceDecoder.DataAddress, 0x90);
        Write(decoder, 12, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 13, PrimaryI2cTraceDecoder.DataAddress, 0xa1);
        Write(decoder, 14, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 15, PrimaryI2cTraceDecoder.ControlAddress, 0xa0);
        Write(decoder, 16, PrimaryI2cTraceDecoder.DataAddress, 0x91);
        Write(decoder, 17, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 18, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        decoder.ObserveRead(
            19,
            0x123,
            PrimaryI2cTraceDecoder.DataAddress,
            0x42);
        Write(decoder, 20, PrimaryI2cTraceDecoder.ControlAddress, 0x98);

        AccessTraceSnapshot snapshot = accumulator.Snapshot();
        Assert.Equal(2, snapshot.Addresses.Count);
        AccessTraceAddressSnapshot write = Assert.Single(
            snapshot.Addresses,
            address =>
                address.Key.Device == 0x92 &&
                address.Key.Register == 0x84);
        Assert.Equal(AccessTraceBus.PrimaryI2c, write.Key.Bus);
        Assert.Equal(1, write.WriteCount);
        Assert.Equal(0x40UL, Assert.Single(
            Assert.Single(write.Operations).Values).Value);
        AccessTraceAddressSnapshot read = Assert.Single(
            snapshot.Addresses,
            address =>
                address.Key.Device == 0x90 &&
                address.Key.Register == 0xa1);
        Assert.Equal(1, read.ReadCount);
        Assert.Equal(0x42UL, Assert.Single(
            Assert.Single(read.Operations).Values).Value);
        AccessTraceReconciliationSnapshot i2cRead = Assert.Single(
            snapshot.Reconciliation,
            item =>
                item.Bus == AccessTraceBus.PrimaryI2c &&
                item.Operation == AccessTraceOperation.Read);
        AccessTraceReconciliationSnapshot i2cWrite = Assert.Single(
            snapshot.Reconciliation,
            item =>
                item.Bus == AccessTraceBus.PrimaryI2c &&
                item.Operation == AccessTraceOperation.Write);
        Assert.Equal((1L, 1L), (i2cRead.CallbackCount, i2cRead.AggregatedCount));
        Assert.Equal((1L, 1L), (i2cWrite.CallbackCount, i2cWrite.AggregatedCount));
    }

    [Fact]
    public void AccessTracePeripheralSessionCapturesArmMmioAndStopsIdempotently()
    {
        using var machine = new MiaMachine(
            [0x00, 0x00],
            modemBih: BuildLoopingModem(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            dedicatedWorkers: false);
        using var trace = new AccessTraceSession(machine, 0, 100);
        uint instructionAddress = machine.Modem!.CurrentInstructionAddress;

        machine.Modem.Bus.WriteByte(0x00800908, 0x12, ArmAccess.None);
        machine.Modem.Bus.WriteByte(0x00800908, 0x34, ArmAccess.None);
        Assert.Throws<InvalidOperationException>(trace.Snapshot);

        trace.Stop();
        trace.Stop();
        trace.Dispose();
        AccessTraceSnapshot snapshot = trace.Snapshot();

        Assert.True(snapshot.Capture.Stopped);
        AccessTraceAddressSnapshot address = Assert.Single(
            snapshot.Addresses,
            item =>
                item.Key.Bus == AccessTraceBus.ArmMmio &&
                item.Key.Address == 0x00800908);
        AccessTraceOperationSnapshot write = Assert.Single(address.Operations);
        Assert.Equal(2, write.Count);
        Assert.Equal(
            new[] { new AccessTraceTransitionCount(0x12, 0x34, 1) },
            write.Transitions);
        Assert.Equal(instructionAddress, write.ProgramCounters[0].InstructionAddress);
        Assert.Null(machine.Cpu.DataReadObserver);
        Assert.Null(machine.Cpu.DataWriteObserver);
    }

    [Fact]
    public void AccessTracePeripheralSessionDecodesTheRealPrimaryControllerBoundary()
    {
        using var machine = new MiaMachine(
            [0x00, 0x00],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            dedicatedWorkers: false);
        using var trace = new AccessTraceSession(machine, 0, 100);

        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.ControlAddress, 0xa0);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.DataAddress, 0x92);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.DataAddress, 0x84);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.DataAddress, 0x40);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        machine.Cpu.WriteData(PrimaryI2cTraceDecoder.ControlAddress, 0x98);
        trace.Stop();

        AccessTraceAddressSnapshot transaction = Assert.Single(
            trace.Snapshot().Addresses,
            address => address.Key.Bus == AccessTraceBus.PrimaryI2c);
        Assert.Equal((byte)0x92, transaction.Key.Device.GetValueOrDefault());
        Assert.Equal((byte)0x84, transaction.Key.Register.GetValueOrDefault());
        Assert.Equal(0x40UL, Assert.Single(
            Assert.Single(transaction.Operations).Values).Value);
    }

    [Fact]
    public void AccessTracePeripheralDecodesEveryMultiByteRegisterPayload()
    {
        AccessTraceAccumulator accumulator = CreateAccumulator(0, 100);
        var decoder = new PrimaryI2cTraceDecoder(accumulator.RecordPrimaryI2c);

        Write(decoder, 1, PrimaryI2cTraceDecoder.ControlAddress, 0xa0);
        Write(decoder, 2, PrimaryI2cTraceDecoder.DataAddress, 0x92);
        Write(decoder, 3, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 4, PrimaryI2cTraceDecoder.DataAddress, 0x20);
        Write(decoder, 5, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 6, PrimaryI2cTraceDecoder.DataAddress, 0x11);
        Write(decoder, 7, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 8, PrimaryI2cTraceDecoder.DataAddress, 0x22);
        Write(decoder, 9, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 10, PrimaryI2cTraceDecoder.DataAddress, 0x33);
        Write(decoder, 11, PrimaryI2cTraceDecoder.ControlAddress, 0x80);
        Write(decoder, 12, PrimaryI2cTraceDecoder.ControlAddress, 0x98);

        IReadOnlyList<AccessTraceAddressSnapshot> writes =
            accumulator.Snapshot().Addresses;
        Assert.Equal(new byte[] { 0x20, 0x21, 0x22 },
            writes.Select(item => item.Key.Register.GetValueOrDefault()));
        Assert.Equal(new ulong[] { 0x11, 0x22, 0x33 },
            writes.Select(item => Assert.Single(
                Assert.Single(item.Operations).Values).Value));
        Assert.All(writes, item => Assert.Equal((byte)0x92, item.Key.Device));
    }

    static void Write(
        PrimaryI2cTraceDecoder decoder,
        long cycle,
        int address,
        byte value) =>
        decoder.ObserveWrite(cycle, 0x123, address, value);

    static AccessTraceAccumulator CreateAccumulator(long start, long end) => new(
        new(start, end),
        new(
            "asic-13mhz",
            13_000_000,
            12_000_000,
            13,
            12,
            0));

    static byte[] BuildLoopingModem()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xeafffffe);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            0xeafffffe);
        byte[] image = new byte[ArmModemImage.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(
            image,
            ArmModemFlash.BaseAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(4),
            (uint)payload.Length);
        payload.CopyTo(image, ArmModemImage.HeaderLength);
        return image;
    }
}
