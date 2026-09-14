// SPDX-License-Identifier: MIT

using Mia.Emulator.Diagnostics;
using Xunit;

namespace AvrCore.Tests;

public sealed class AccessTraceAvrTests
{
    [Fact]
    public void AccessTraceAvrInstructionObserverSeesExecutingWordAddress()
    {
        var cpu = new Cpu(new byte[16], 0x6000);
        var program = Assembler.Assemble(
            "LDS r5, 0x1234\nSTS 0x1235, r6");
        Assert.Empty(program.Errors);
        program.Bytes.CopyTo(cpu.ProgBytes, 0);
        cpu.Data[0x1234] = 0x5a;
        cpu.Data[6] = 0xa5;
        var observed = new List<(AccessTraceOperation Operation, int Pc)>();
        cpu.DataReadObserver = (_, _) =>
            observed.Add((AccessTraceOperation.Read, cpu.PC));
        cpu.DataWriteObserver = (_, _, _, _, _) =>
            observed.Add((AccessTraceOperation.Write, cpu.PC));

        AvrInstruction.Execute(cpu);
        AvrInstruction.Execute(cpu);

        Assert.Equal(
            new[]
            {
                (AccessTraceOperation.Read, 0),
                (AccessTraceOperation.Write, 2),
            },
            observed);
        Assert.Equal(4, cpu.PC);
    }

    [Fact]
    public void AccessTraceAvrCentralObserversCoverHooksMasksRegistersAndFarData()
    {
        var cpu = new Cpu(new byte[4], 0x130000);
        var reads = new List<(int Address, byte Value)>();
        var writes = new List<(
            int Address,
            byte OldValue,
            byte NewValue,
            byte Mask,
            bool HookConsumed)>();
        cpu.DataReadObserver = (address, value) => reads.Add((address, value));
        cpu.DataWriteObserver =
            (address, oldValue, newValue, mask, hookConsumed) =>
                writes.Add((address, oldValue, newValue, mask, hookConsumed));
        cpu.Data[5] = 0x15;
        cpu.ReadHooks[5] = _ => 0xee;
        cpu.ReadHooks[0x40] = _ => 0x7a;
        cpu.Data[0x41] = 0xf0;
        cpu.WriteHooks[0x41] = (_, _, _, _) => true;
        cpu.Data[0x42] = 0xf0;

        Assert.Equal(0x15, cpu.ReadData(5));
        Assert.Equal(0x7a, cpu.ReadData(0x40));
        cpu.WriteData(0x41, 0x0f, 0x0f);
        cpu.WriteData(0x42, 0x0f, 0x0f);
        cpu.WriteData(0x123456, 0xa5);

        Assert.Equal(
            new[] { (5, (byte)0x15), (0x40, (byte)0x7a) },
            reads);
        Assert.Equal(3, writes.Count);
        Assert.Equal(
            (0x41, (byte)0xf0, (byte)0x0f, (byte)0x0f, true),
            writes[0]);
        Assert.Equal(
            (0x42, (byte)0xf0, (byte)0x0f, (byte)0x0f, false),
            writes[1]);
        Assert.Equal(
            (0x123456, (byte)0x00, (byte)0xa5, (byte)0xff, false),
            writes[2]);
        Assert.Equal(0xf0, cpu.Data[0x41]);
        Assert.Equal(0xff, cpu.Data[0x42]);
        Assert.Equal(0xa5, cpu.Data[0x123456]);
    }

    [Fact]
    public void AccessTraceAvrRangeHooksCoverLargeMemoryWithoutPerByteHooks()
    {
        var cpu = new Cpu(new byte[4], 0x130000);
        var writes = new List<int>();
        cpu.AddReadHookRange(0x100000, 0x120000, _ => 0x5a);
        cpu.AddWriteHookRange(0x100000, 0x120000, (_, _, address, _) =>
        {
            writes.Add(address);
            return true;
        });
        cpu.ReadHooks[0x100100] = _ => 0xa5;

        Assert.True(cpu.HasReadHook(0x100000));
        Assert.True(cpu.HasReadHook(0x100100));
        Assert.Equal(0x5a, cpu.ReadData(0x100000));
        Assert.Equal(0xa5, cpu.ReadData(0x100100));
        cpu.WriteData(0x100000, 0x7e);

        Assert.Equal(new[] { 0x100000 }, writes);
        Assert.Equal(0, cpu.Data[0x100000]);
    }

    [Fact]
    public void AccessTraceAvrCanMapHookedAddressesOutsidePhysicalData()
    {
        var cpu = new Cpu(
            new byte[4],
            sramBytes: 0x2000,
            dataAddressSpaceSize: 0x1000000);
        cpu.AddReadHookRange(0x800000, 0x800100, _ => 0x5a);
        cpu.AddWriteHookRange(0x800000, 0x800100, (_, _, _, _) => true);

        Assert.Equal(0x5a, cpu.ReadData(0x800000));
        cpu.WriteData(0x800000, 0x99);
        Assert.Equal(0x2100, cpu.Data.Length);
        Assert.Equal(0x1000000, cpu.DataAddressSpaceSize);
    }

    [Fact]
    public void AccessTraceAvrAccumulatorReconcilesEverySufficientStatistic()
    {
        AccessTraceAccumulator accumulator = CreateAccumulator(0, 2_048);
        accumulator.RecordAvrRead(0, 0x100, 0x123456, 0);
        accumulator.RecordAvrRead(1, 0x100, 0x123456, 1);
        accumulator.RecordAvrWrite(
            8,
            0x200,
            0x123456,
            oldValue: 0xf0,
            newValue: 0x0f,
            mask: 0x0f);
        accumulator.RecordAvrWrite(
            9,
            0x201,
            0x123456,
            oldValue: 0xff,
            newValue: 0xfe,
            mask: 0x01);

        AccessTraceSnapshot snapshot = accumulator.Snapshot();
        AccessTraceAddressSnapshot address = Assert.Single(snapshot.Addresses);
        AssertSufficientStatistics(snapshot, address);
    }

    static void AssertSufficientStatistics(
        AccessTraceSnapshot snapshot,
        AccessTraceAddressSnapshot address)
    {
        Assert.Equal(4, address.TotalCount);
        Assert.Equal(2, address.ReadCount);
        Assert.Equal(2, address.WriteCount);
        Assert.Equal(3, address.ChangeCount);
        Assert.Equal(0, address.FirstCycle);
        Assert.Equal(9, address.LastCycle);
        AccessTraceOperationSnapshot read = Assert.Single(
            address.Operations,
            operation => operation.Operation == AccessTraceOperation.Read);
        AccessTraceOperationSnapshot write = Assert.Single(
            address.Operations,
            operation => operation.Operation == AccessTraceOperation.Write);
        Assert.Equal(
            new[] { new AccessTraceCount(0, 1), new AccessTraceCount(1, 1) },
            read.Values);
        Assert.Equal(
            new[] { new AccessTraceTransitionCount(0, 1, 1) },
            read.Transitions);
        Assert.Equal(0xf0UL, write.InitialHeldValue);
        Assert.Equal(
            new[] { new AccessTraceCount(0xfe, 1), new AccessTraceCount(0xff, 1) },
            write.Values);
        Assert.Equal(
            new[]
            {
                new AccessTraceTransitionCount(0xf0, 0xff, 1),
                new AccessTraceTransitionCount(0xff, 0xfe, 1),
            },
            write.Transitions);
        Assert.Equal(new AccessTraceBitEdgeCount(0, 1, 1), write.BitEdges[0]);
        Assert.Equal(new AccessTraceBitEdgeCount(1, 1, 0), write.BitEdges[1]);
        Assert.Equal(new AccessTraceBitEdgeCount(2, 1, 0), write.BitEdges[2]);
        Assert.Equal(new AccessTraceBitEdgeCount(3, 1, 0), write.BitEdges[3]);
        Assert.Equal(
            new[]
            {
                new AccessTracePcCount(0x200, 1),
                new AccessTracePcCount(0x201, 1),
            },
            write.ProgramCounters);
        Assert.Equal(
            new[]
            {
                new AccessTraceCount(0x01, 1),
                new AccessTraceCount(0x0f, 1),
            },
            write.Masks);
        Assert.Equal(2_048, AccessTraceSeriesBuilder.ExpandHeldValues(write).Length);
        Assert.Equal(0xf0UL, AccessTraceSeriesBuilder.ExpandHeldValues(write)[0]);
        Assert.Equal(0xffUL, AccessTraceSeriesBuilder.ExpandHeldValues(write)[8]);
        Assert.Equal(0xfeUL, AccessTraceSeriesBuilder.ExpandHeldValues(write)[9]);
        Assert.All(
            snapshot.Reconciliation,
            item => Assert.Equal(item.CallbackCount, item.AggregatedCount));
    }

    [Fact]
    public void AccessTraceAvrHeldExpansionDistinguishesNoAccessFromZero()
    {
        AccessTraceAccumulator accumulator = CreateAccumulator(0, 2_048);
        accumulator.RecordAvrRead(5, 7, 0x40, 0);

        AccessTraceOperationSnapshot read = Assert.Single(
            Assert.Single(accumulator.Snapshot().Addresses).Operations);
        ulong?[] held = AccessTraceSeriesBuilder.ExpandHeldValues(read);

        Assert.Null(held[4]);
        Assert.Equal(0UL, held[5]);
        Assert.Equal(0UL, held[^1]);
    }

    [Fact]
    public void AccessTraceAvrConsumedWritesUseCommandTransitionsNotBackingState()
    {
        AccessTraceAccumulator accumulator = CreateAccumulator(0, 2_048);
        accumulator.RecordAvrWrite(
            1,
            0x300,
            0x800000,
            oldValue: 0xaa,
            newValue: 0x10,
            mask: 0xff,
            hookConsumed: true);
        accumulator.RecordAvrWrite(
            2,
            0x301,
            0x800000,
            oldValue: 0xaa,
            newValue: 0x20,
            mask: 0xff,
            hookConsumed: true);

        AccessTraceOperationSnapshot write = Assert.Single(
            Assert.Single(accumulator.Snapshot().Addresses).Operations);

        Assert.Equal(2, write.ConsumedWriteCount);
        Assert.Null(write.InitialHeldValue);
        Assert.Equal(
            new[] { new AccessTraceTransitionCount(0x10, 0x20, 1) },
            write.Transitions);
        Assert.DoesNotContain(
            write.Transitions,
            transition => transition.OldValue == 0xaa);
    }

    static AccessTraceAccumulator CreateAccumulator(long start, long end) => new(
        new(start, end),
        new(
            "asic-13mhz",
            13_000_000,
            12_000_000,
            13,
            12,
            0));
}
