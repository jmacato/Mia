// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicFirmwareFastPathsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(256)]
    [InlineData(ushort.MaxValue)]
    public void BusyWaitFastPathMatchesNativeLoop(ushort count)
    {
        var program = new byte[(AsicFirmwareFastPaths.BusyWaitReturnWord + 1) * 2];
        var native = new Cpu((byte[])program.Clone());
        var fast = new Cpu((byte[])program.Clone());
        foreach (var cpu in new[] { native, fast })
        {
            cpu.SetProgWord(0x018908, 0x5001); // SUBI r16, 1
            cpu.SetProgWord(0x018909, 0x4010); // SBCI r17, 0
            cpu.SetProgWord(0x01890a, 0xf7b1); // BRNE 0x018901
            cpu.PC = AsicFirmwareFastPaths.BusyWaitLoopWord;
            cpu.SetUint16(16, count);
            cpu.Data[0x5f] = 0xd5;
            cpu.Cycles = 123;
        }

        while (native.PC != AsicFirmwareFastPaths.BusyWaitReturnWord)
        {
            AvrInstruction.Execute(native);
        }

        Assert.True(AsicFirmwareFastPaths.TrySkipBusyWait(fast));
        Assert.Equal(native.PC, fast.PC);
        Assert.Equal(native.Cycles, fast.Cycles);
        Assert.Equal(native.Data[16], fast.Data[16]);
        Assert.Equal(native.Data[17], fast.Data[17]);
        Assert.Equal(native.SREG, fast.SREG);
    }

    [Fact]
    public void BusyWaitFastPathRejectsOtherPcAndImpossibleZeroCount()
    {
        var cpu = new Cpu(new byte[0x40000]);
        cpu.PC = AsicFirmwareFastPaths.BusyWaitLoopWord - 1;
        cpu.SetUint16(16, 1);
        Assert.False(AsicFirmwareFastPaths.TrySkipBusyWait(cpu));

        cpu.PC = AsicFirmwareFastPaths.BusyWaitLoopWord;
        cpu.SetUint16(16, 0);
        Assert.False(AsicFirmwareFastPaths.TrySkipBusyWait(cpu));
    }

    [Fact]
    public void GdfsUnitStateScanStopsAtSameFirstNonzeroLogicalByte()
    {
        var program = new byte[0x800000];
        var cpu = new Cpu(program, 0x1000000);
        program[0x46c581] = 1;
        program[0x46c584] = 0x80;
        _ = new AsicFlashMemory(cpu);
        cpu.PC = AsicFirmwareFastPaths.GdfsUnitStateScanWord;
        cpu.Data[19] = 0xc6;
        cpu.Data[0x5b] = 0xc6;
        cpu.SetUint16(30, 0xc586);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsUnitStateZeroRun(cpu));

        Assert.Equal(0xc584, cpu.GetUint16(30));
        Assert.Equal(AsicFirmwareFastPaths.GdfsUnitStateFoundWord, cpu.PC);
    }

    [Fact]
    public void GdfsUnitStateScanRejectsWrongBankAndUnmappedUnderflow()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        cpu.PC = AsicFirmwareFastPaths.GdfsUnitStateScanWord;
        cpu.Data[0x5b] = 0xc5;
        cpu.SetUint16(30, 0xc581);
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsUnitStateZeroRun(cpu));

        cpu.Data[0x5b] = 0xc6;
        cpu.Data[19] = 0xc6;
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsUnitStateZeroRun(cpu));
        Assert.Equal(AsicFirmwareFastPaths.GdfsUnitStateScanWord, cpu.PC);
    }

    [Fact]
    public void BoundedUnitStateScanMatchesNativeRegistersFlagsAndCycles()
    {
        byte[] program = new byte[0x800000];
        var words = MiaFirmwareRuntimeHooks.GdfsUnitStateLoopWords;
        for (int i = 0; i < words.Length; i++)
        {
            int address = (AsicFirmwareFastPaths.GdfsUnitStateScanWord + i) * 2;
            program[address] = (byte)words[i];
            program[address + 1] = (byte)(words[i] >> 8);
        }
        var native = new Cpu(program, 0x1000000);
        var fast = new Cpu(program, 0x1000000);
        _ = new AsicFlashMemory(native);
        _ = new AsicFlashMemory(fast);
        foreach (byte value in new byte[] { 1, 0x80 })
        foreach (int startPointer in new[] { 0xc584, 0xc586 })
        foreach (int deadline in new[] { 8, 9, 18, 27 })
        for (int flags = 0; flags <= 255; flags++)
        {
            program[0x46c584] = value;
            foreach (var cpu in new[] { native, fast })
            {
                cpu.Data.AsSpan(0, 0x60).Clear();
                cpu.Cycles = 0;
                cpu.PC = AsicFirmwareFastPaths.GdfsUnitStateScanWord;
                cpu.Data[19] = 0xc6;
                cpu.SetUint16(30, startPointer);
                cpu.SetStatusRegister((byte)flags);
            }
            bool skipped = AsicFirmwareFastPaths.TrySkipGdfsUnitStateZeroRun(
                fast, deadline, out int instructions);
            int executed = 0;
            while (native.Cycles < fast.Cycles)
            {
                AvrInstruction.Execute(native);
                executed++;
            }
            Assert.Equal(skipped, instructions != 0);
            Assert.Equal(instructions, executed);
            Assert.InRange(fast.Cycles, 0, deadline);
            Assert.Equal(native.Cycles, fast.Cycles);
            Assert.Equal(native.PC, fast.PC);
            Assert.Equal(native.Data.AsSpan(0, 0x60).ToArray(),
                fast.Data.AsSpan(0, 0x60).ToArray());
        }
    }

    [Fact]
    public void GdfsErasedRangeScanStopsOnSameFirstProgrammedByte()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        var cpu = new Cpu(program, 0x1000000);
        var flash = new AsicFlashMemory(cpu);
        program[0x580006] = 0x12;
        cpu.PC = AsicFirmwareFastPaths.GdfsErasedRangeScanWord;
        cpu.Data[0] = 8;
        cpu.Data[18] = 0xd8;
        cpu.Data[19] = 0xd8;
        cpu.Data[20] = 4;
        cpu.SetUint16(30, 4);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsErasedRange(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsErasedRangeFoundWord, cpu.PC);
        Assert.Equal(6, cpu.Data[20]);
        Assert.Equal(6, cpu.GetUint16(30));
        Assert.Equal(0x12, cpu.Data[17]);
        Assert.Equal(0xd8, cpu.Data[0x5b]);
        Assert.Equal(3, flash.ReadCount);
    }

    [Fact]
    public void GdfsErasedRangeScanPreservesCompletedCursorAndWrappedPointer()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        var cpu = new Cpu(program, 0x1000000);
        var flash = new AsicFlashMemory(cpu);
        cpu.PC = AsicFirmwareFastPaths.GdfsErasedRangeScanWord;
        cpu.Data[2] = 1;
        cpu.Data[18] = 0xd8;
        cpu.Data[19] = 0xd8;
        cpu.Data[20] = 0xfc;
        cpu.Data[21] = 0xff;
        cpu.SetUint16(30, 0xfffc);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsErasedRange(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsErasedRangeCompleteWord, cpu.PC);
        Assert.Equal(0, cpu.Data[20]);
        Assert.Equal(0, cpu.Data[21]);
        Assert.Equal(1, cpu.Data[22]);
        Assert.Equal(0, cpu.GetUint16(30));
        Assert.Equal(0xd8, cpu.Data[0x5b]);
        Assert.Equal(4, flash.ReadCount);
    }

    [Fact]
    public void GdfsSectorHeaderScanStopsAtFirstMatchingKeyForNativeHandling()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        var cpu = new Cpu(program, 0x1000000);
        var flash = new AsicFlashMemory(cpu);
        program[0x590401] = 0x34;
        program[0x590402] = 0x12;
        program[0x59040a] = 0xc9;
        program[0x59040b] = 0x04;
        cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
        cpu.Data[6] = 0xc9;
        cpu.Data[7] = 0x04;
        cpu.Data[20] = 0;
        cpu.Data[21] = 4;
        cpu.Data[24] = 1;

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsSectorHeaderMatchWord, cpu.PC);
        Assert.Equal(0x0409u, GetUInt32(cpu, 20));
        Assert.Equal(0x0409, cpu.GetUint16(30));
        Assert.Equal(0xd9, cpu.Data[0x5b]);
        Assert.Equal(0xc9, cpu.Data[0]);
        Assert.Equal(0x04, cpu.Data[1]);
        Assert.Equal(4, flash.ReadCount);
    }

    [Fact]
    public void GdfsSectorHeaderFastPathMatchesTheFirmwareMismatchAndMatchRun()
    {
        byte[] nativeProgram = BuildGdfsSectorHeaderLoopProgram();
        byte[] fastProgram = (byte[])nativeProgram.Clone();
        var native = new Cpu(nativeProgram, 0x1000000);
        var fast = new Cpu(fastProgram, 0x1000000);
        _ = new AsicFlashMemory(native);
        _ = new AsicFlashMemory(fast);
        foreach (var cpu in new[] { native, fast })
        {
            cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
            cpu.Cycles = 123;
            cpu.Data[4] = 0;
            cpu.Data[6] = 0xc9;
            cpu.Data[7] = 0x04;
            cpu.Data[20] = 0;
            cpu.Data[21] = 4;
            cpu.Data[24] = 1;
            cpu.Data[0x5f] = 0xa1;
        }

        while (native.PC != AsicFirmwareFastPaths.GdfsSectorHeaderMatchWord)
        {
            AvrInstruction.Execute(native);
        }

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(fast));
        Assert.Equal(native.PC, fast.PC);
        Assert.Equal(native.Cycles, fast.Cycles);
        Assert.Equal(native.Data, fast.Data);
    }

    [Fact]
    public void GdfsSectorHeaderFastPathStopsBeforeItsCycleDeadline()
    {
        byte[] nativeProgram = BuildGdfsSectorHeaderLoopProgram();
        byte[] fastProgram = (byte[])nativeProgram.Clone();
        var native = new Cpu(nativeProgram, 0x1000000);
        var fast = new Cpu(fastProgram, 0x1000000);
        _ = new AsicFlashMemory(native);
        _ = new AsicFlashMemory(fast);
        foreach (var cpu in new[] { native, fast })
        {
            cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
            cpu.Cycles = 123;
            cpu.Data[4] = 0;
            cpu.Data[6] = 0xc9;
            cpu.Data[7] = 0x04;
            cpu.Data[20] = 0;
            cpu.Data[21] = 4;
            cpu.Data[24] = 1;
            cpu.Data[0x5f] = 0xa1;
        }

        var nativeInstructions = 0;
        do
        {
            AvrInstruction.Execute(native);
            nativeInstructions++;
        }
        while (native.PC != AsicFirmwareFastPaths.GdfsSectorHeaderScanWord);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(
            fast,
            maximumCycles: 36,
            out int fastInstructions));
        Assert.Equal(nativeInstructions, fastInstructions);
        Assert.Equal(native.PC, fast.PC);
        Assert.Equal(native.Cycles, fast.Cycles);
        Assert.Equal(native.Data, fast.Data);
    }

    [Fact]
    public void GdfsSectorHeaderScanPreservesExhaustedCursorForNativeNotFoundPath()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        var cpu = new Cpu(program, 0x1000000);
        var flash = new AsicFlashMemory(cpu);
        cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
        cpu.Data[6] = 0xc9;
        cpu.Data[7] = 0x04;
        cpu.Data[20] = 0xf7;
        cpu.Data[21] = 0xff;

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsSectorHeaderScanWord, cpu.PC);
        Assert.Equal(0x10000u, GetUInt32(cpu, 20));
        Assert.Equal(0xfff7, cpu.GetUint16(30));
        Assert.Equal(0xd8, cpu.Data[0x5b]);
        Assert.Equal(2, flash.ReadCount);
    }

    [Fact]
    public void GdfsSectorHeaderScanRejectsFoundOrOutOfRangeState()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
        cpu.Data[4] = 1;
        cpu.Data[20] = 0;
        cpu.Data[21] = 4;
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(cpu));

        cpu.Data[4] = 0;
        cpu.Data[20] = 0;
        cpu.Data[21] = 0;
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsSectorHeaderMismatchRun(cpu));
    }

    [Fact]
    public void GdfsEntryHeaderScanLeavesFirstMatchForNativeHandling()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        var cpu = new Cpu(program, 0x1000000);
        var flash = new AsicFlashMemory(cpu);
        const uint cursor = 0x0400;
        const int physicalBase = 0x580000;
        cpu.PC = AsicFirmwareFastPaths.GdfsEntryHeaderScanWord;
        cpu.Data[4] = 0;
        cpu.SetUint16(28, 0x1000);
        cpu.Data[0x5a] = 0;
        cpu.WriteData(0x1004, 0x34);
        cpu.WriteData(0x1005, 0x12);
        SetUInt32(cpu, 24, cursor);
        program[physicalBase + (int)cursor + 9] = 0x0f;
        program[physicalBase + (int)cursor + 18] = 0x10;
        program[physicalBase + (int)cursor + 19] = 0x78;
        program[physicalBase + (int)cursor + 20] = 0x56;
        program[physicalBase + (int)cursor + 27] = 0x10;
        program[physicalBase + (int)cursor + 28] = 0x34;
        program[physicalBase + (int)cursor + 29] = 0x12;

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsEntryHeaderMismatchRun(cpu));

        Assert.Equal(cursor + 18, GetUInt32(cpu, 24));
        Assert.Equal(AsicFirmwareFastPaths.GdfsEntryHeaderScanWord, cpu.PC);
        Assert.Equal(8, flash.ReadCount);
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsEntryHeaderMismatchRun(cpu));
    }

    [Fact]
    public void GdfsEntryHeaderScanLeavesTerminalIterationForNativeExit()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        var cpu = new Cpu(program, 0x1000000);
        _ = new AsicFlashMemory(cpu);
        cpu.PC = AsicFirmwareFastPaths.GdfsEntryHeaderScanWord;
        cpu.SetUint16(28, 0x1000);
        cpu.WriteData(0x1004, 0x34);
        cpu.WriteData(0x1005, 0x12);
        SetUInt32(cpu, 24, 0xffdc);
        program[0x58ffe5] = 0x0f;
        program[0x58ffee] = 0x10;

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsEntryHeaderMismatchRun(cpu));

        Assert.Equal(0xffeeu, GetUInt32(cpu, 24));
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsEntryHeaderMismatchRun(cpu));
    }

    [Fact]
    public void GdfsEntryHeaderScanRejectsOtherPcAndCompletedCursor()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        cpu.PC = AsicFirmwareFastPaths.GdfsEntryHeaderScanWord - 1;
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsEntryHeaderMismatchRun(cpu));

        cpu.PC = AsicFirmwareFastPaths.GdfsEntryHeaderScanWord;
        SetUInt32(cpu, 24, 0x10000);
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsEntryHeaderMismatchRun(cpu));
    }

    [Fact]
    public void GdfsLinkedRecordScanStopsAtFirstMatchingKeyForNativeHandling()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        const int target = 0x001000;
        const int first = 0x021100;
        const int match = 0x021200;
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedRecordScanWord;
        SetUInt24(cpu, 0, target);
        SetUInt24(cpu, 20, first);
        cpu.WriteData(target + 3, 0x34);
        cpu.WriteData(target + 4, 0x12);
        SetUInt24(cpu, first + 3, match);
        cpu.WriteData(first + 6, 0x78);
        cpu.WriteData(first + 7, 0x56);
        cpu.WriteData(match + 6, 0x34);
        cpu.WriteData(match + 7, 0x12);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsLinkedRecordMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsLinkedRecordMatchWord, cpu.PC);
        Assert.Equal(match, GetUInt24(cpu, 20));
        Assert.Equal(0x1200, cpu.GetUint16(30));
        Assert.Equal(0x02, cpu.Data[0x5b]);
        Assert.Equal(0x34, cpu.Data[24]);
        Assert.Equal(0x12, cpu.Data[25]);
        Assert.Equal(0x34, cpu.Data[26]);
        Assert.Equal(0x12, cpu.Data[27]);
    }

    [Fact]
    public void GdfsLinkedRecordScanPreservesNullEndForNativeExit()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        const int target = 0x001000;
        const int only = 0x021100;
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedRecordScanWord;
        SetUInt24(cpu, 0, target);
        SetUInt24(cpu, 20, only);
        cpu.WriteData(target + 3, 0x34);
        cpu.WriteData(target + 4, 0x12);
        cpu.WriteData(only + 6, 0x78);
        cpu.WriteData(only + 7, 0x56);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsLinkedRecordMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsLinkedRecordScanWord, cpu.PC);
        Assert.Equal(0, GetUInt24(cpu, 20));
    }

    [Fact]
    public void GdfsLinkedRecordScanYieldsAtCircularListBoundary()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        const int target = 0x001000;
        const int first = 0x021100;
        const int second = 0x021200;
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedRecordScanWord;
        SetUInt24(cpu, 0, target);
        SetUInt24(cpu, 20, first);
        cpu.WriteData(target + 3, 0x34);
        cpu.WriteData(target + 4, 0x12);
        SetUInt24(cpu, first + 3, second);
        SetUInt24(cpu, second + 3, first);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsLinkedRecordMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsLinkedRecordScanWord, cpu.PC);
        Assert.Equal(first, GetUInt24(cpu, 20));
    }

    [Fact]
    public void GdfsLinkedRecordScanRejectsCompletedState()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedRecordScanWord;
        SetUInt24(cpu, 0, 0x001000);
        SetUInt24(cpu, 20, 0x002000);
        cpu.Data[3] = 1;
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsLinkedRecordMismatchRun(cpu));

        cpu.Data[3] = 0;
        SetUInt24(cpu, 20, 0);
        Assert.False(AsicFirmwareFastPaths.TrySkipGdfsLinkedRecordMismatchRun(cpu));
    }

    [Fact]
    public void GdfsLinkedExtentScanStopsAtFirstMatchingKeyForNativeHandling()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        const int target = 0x031000;
        const int first = 0x021100;
        const int match = 0x021200;
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedExtentScanWord;
        cpu.Data[26] = (byte)(target & 0xff);
        cpu.Data[27] = (byte)(target >> 8 & 0xff);
        cpu.Data[25] = (byte)(target >> 16);
        SetUInt24(cpu, 16, first);
        cpu.WriteData(target + 3, 0x34);
        cpu.WriteData(target + 4, 0x12);
        SetUInt24(cpu, first, match);
        cpu.WriteData(first + 6, 0x78);
        cpu.WriteData(first + 7, 0x56);
        cpu.WriteData(match + 6, 0x34);
        cpu.WriteData(match + 7, 0x12);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsLinkedExtentMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsLinkedExtentMatchWord, cpu.PC);
        Assert.Equal(match, GetUInt24(cpu, 16));
        Assert.Equal(0x1200, cpu.GetUint16(30));
        Assert.Equal(0x02, cpu.Data[0x5b]);
        Assert.Equal(0x34, cpu.Data[22]);
        Assert.Equal(0x12, cpu.Data[23]);
        Assert.Equal(0x34, cpu.Data[0]);
        Assert.Equal(0x12, cpu.Data[1]);
    }

    [Fact]
    public void GdfsLinkedExtentScanPreservesNullEndForNativeExit()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        const int target = 0x031000;
        const int only = 0x021100;
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedExtentScanWord;
        cpu.Data[26] = (byte)(target & 0xff);
        cpu.Data[27] = (byte)(target >> 8 & 0xff);
        cpu.Data[25] = (byte)(target >> 16);
        SetUInt24(cpu, 16, only);
        cpu.WriteData(target + 3, 0x34);
        cpu.WriteData(target + 4, 0x12);
        cpu.WriteData(only + 6, 0x78);
        cpu.WriteData(only + 7, 0x56);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsLinkedExtentMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsLinkedExtentScanWord, cpu.PC);
        Assert.Equal(0, GetUInt24(cpu, 16));
    }

    [Fact]
    public void GdfsLinkedExtentScanYieldsAtCircularListBoundary()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000);
        const int target = 0x031000;
        const int first = 0x021100;
        const int second = 0x021200;
        cpu.PC = AsicFirmwareFastPaths.GdfsLinkedExtentScanWord;
        cpu.Data[26] = (byte)(target & 0xff);
        cpu.Data[27] = (byte)(target >> 8 & 0xff);
        cpu.Data[25] = (byte)(target >> 16);
        SetUInt24(cpu, 16, first);
        cpu.WriteData(target + 3, 0x34);
        cpu.WriteData(target + 4, 0x12);
        SetUInt24(cpu, first, second);
        SetUInt24(cpu, second, first);

        Assert.True(AsicFirmwareFastPaths.TrySkipGdfsLinkedExtentMismatchRun(cpu));

        Assert.Equal(AsicFirmwareFastPaths.GdfsLinkedExtentScanWord, cpu.PC);
        Assert.Equal(first, GetUInt24(cpu, 16));
    }

    static uint GetUInt32(Cpu cpu, int register) =>
        (uint)(cpu.Data[register] |
            cpu.Data[register + 1] << 8 |
            cpu.Data[register + 2] << 16 |
            cpu.Data[register + 3] << 24);

    static byte[] BuildGdfsSectorHeaderLoopProgram()
    {
        var program = Enumerable.Repeat((byte)0xff, 0x800000).ToArray();
        ushort[] words =
        [
            0x2044, 0xf009, 0x0000, 0x3040, 0xe000, 0x0750,
            0xe001, 0x0760, 0xe000, 0x0770, 0xf5e0, 0x2f28,
            0x2711, 0x0f04, 0x1f15, 0x1f26, 0x5000, 0x4010,
            0x4228, 0x2fe0, 0x2ff1, 0xbf2b, 0x8001, 0x8012,
            0x1406, 0x0417, 0xf539,
        ];
        for (int index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                program.AsSpan(
                    (AsicFirmwareFastPaths.GdfsSectorHeaderScanWord + index) * 2),
                words[index]);
        }
        ushort[] tail = [0x5f47, 0x4f5f, 0x4f6f, 0x4f7f, 0xcfb9];
        for (int index = 0; index < tail.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                program.AsSpan((0x0cb4e6 + index) * 2), tail[index]);
        }
        program[0x590401] = 0x34;
        program[0x590402] = 0x12;
        program[0x59040a] = 0xc9;
        program[0x59040b] = 0x04;
        return program;
    }

    static void SetUInt32(Cpu cpu, int register, uint value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
        cpu.Data[register + 2] = (byte)(value >> 16);
        cpu.Data[register + 3] = (byte)(value >> 24);
    }

    static int GetUInt24(Cpu cpu, int register) =>
        cpu.Data[register] |
        cpu.Data[register + 1] << 8 |
        cpu.Data[register + 2] << 16;

    static void SetUInt24(Cpu cpu, int register, int value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
        cpu.Data[register + 2] = (byte)(value >> 16);
    }
}
