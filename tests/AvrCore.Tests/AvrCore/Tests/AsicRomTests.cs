// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicRomTests
{
    [Fact]
    public void TestProgramSignatureServiceCompletesThroughItsVoidAbi()
    {
        const int request = 0x1016a5;
        var cpu = CreateCpu(0x3f8000, 0x12345, 0x110000);
        SetUInt24(cpu, 16, request);
        cpu.Data[0x0b60] = 0xa5;
        for (var index = 0; index < 96; index++)
        {
            cpu.Data[request + index] = (byte)(index ^ 0x5a);
        }
        var registersBefore = cpu.Data[..32].ToArray();
        var requestBefore = cpu.Data[request..(request + 96)].ToArray();

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(0x1000, cpu.SP);
        Assert.Equal(registersBefore, cpu.Data[..32]);
        Assert.Equal(requestBefore, cpu.Data[request..(request + 96)]);
        Assert.Equal(0xa5, cpu.Data[0x0b60]);
    }

    [Fact]
    public void SixteenByteBlockCopyConsumesCountAndCopiesExtendedData()
    {
        const int source = 0x21000;
        const int destination = 0x22000;
        const int stack = 0x2400;
        var cpu = CreateCpu(0x3f0152, 0x12345);
        for (var index = 0; index < 32; index++)
        {
            cpu.Data[source + index] = (byte)(0x80 + index);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, source);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = 0;
        cpu.SetUint16(stack, 2);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(stack + 2, cpu.GetUint16(28));
        Assert.Equal(cpu.Data[source..(source + 32)], cpu.Data[destination..(destination + 32)]);
    }

    [Fact]
    public void ThirtyTwoByteBlockCopyConsumesCountFromExtendedSoftwareStack()
    {
        const int source = 0xc3bdf2;
        const int destination = 0x108d03;
        const int stack = 0xd6e44b;
        var cpu = CreateCpu(0x3f0156, 0x12345, 0x1000000);
        for (var index = 0; index < 64; index++)
        {
            cpu.Data[source + index] = (byte)(index ^ 0xa5);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, source);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = (byte)(stack >> 16);
        cpu.SetUint16(stack, 2);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(stack + 2, cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
        Assert.Equal(cpu.Data[source..(source + 64)], cpu.Data[destination..(destination + 64)]);
    }

    [Theory]
    [InlineData(0x3f013a, 4)]
    [InlineData(0x3f0142, 8)]
    public void SmallBlockCopyFamilyUsesTwentyFourBitPointersAndSixteenBitCount(
        int entry, int blockSize)
    {
        const int source = 0xc3be12;
        const int destination = 0x108d23;
        const int stack = 0xd6e44b;
        var cpu = CreateCpu(entry, 0x12345, 0x1000000);
        for (var index = 0; index < blockSize * 2; index++)
        {
            cpu.Data[source + index] = (byte)(index ^ 0x3c);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, source);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = (byte)(stack >> 16);
        cpu.SetUint16(stack, 2);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(stack + 2, cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
        Assert.Equal(
            cpu.Data[source..(source + blockSize * 2)],
            cpu.Data[destination..(destination + blockSize * 2)]);
    }

    [Fact]
    public void EightBitMultiplyReturnsLowByteAndPreservesHighRegister()
    {
        var cpu = CreateCpu(0x3f0044, 0x123);
        cpu.Data[16] = 0x7f;
        cpu.Data[17] = 0xa5;
        cpu.Data[20] = 3;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x7d, cpu.Data[16]);
        Assert.Equal(0xa5, cpu.Data[17]);
    }

    [Fact]
    public void PrologueSavesAbiCalleeRegistersAndAllocatesFull24BitYFrame()
    {
        var cpu = CreateCpu(romEntry: 0x3f000a, returnAddress: 0x12345);
        cpu.Data[0x5a] = 2;
        cpu.SetUint16(28, 2);
        var savedRegisters = new[] { 25, 26, 27, 24, 4 };
        for (var index = 0; index < savedRegisters.Length; index++)
        {
            cpu.Data[savedRegisters[index]] = (byte)(0xa0 + index);
        }
        cpu.Data[0] = 0xee;

        var result = AsicRom.Dispatch(cpu);

        Assert.Equal(AsicRomDispatchResult.Handled, result);
        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(0x1000, cpu.SP);
        Assert.Equal(1, cpu.Data[0x5a]);
        Assert.Equal(0xfffd, cpu.GetUint16(28));
        for (var index = 0; index < savedRegisters.Length; index++)
        {
            Assert.Equal(0xa0 + index, cpu.Data[0x1fffd + index]);
        }
        Assert.Equal(0, cpu.Data[0x20002]);
        Assert.Equal(0xee, cpu.Data[0]);
    }

    [Fact]
    public void EpilogueRestoresAbiCalleeRegistersAndCleansFrameAndArguments()
    {
        var cpu = CreateCpu(romEntry: 0x3f002c, returnAddress: 0x23456);
        cpu.Data[0x5a] = 1;
        cpu.SetUint16(28, 0xfffd);
        var savedRegisters = new[] { 25, 26, 27, 24, 4 };
        for (var index = 0; index < savedRegisters.Length; index++)
        {
            cpu.Data[savedRegisters[index]] = 0xee;
            cpu.Data[0x1fffd + index] = (byte)(0xb0 + index);
        }
        cpu.Data[0] = 0xdd;
        cpu.Data[30] = 10;

        var result = AsicRom.Dispatch(cpu);

        Assert.Equal(AsicRomDispatchResult.Handled, result);
        Assert.Equal(0x23456, cpu.PC);
        Assert.Equal(0x1000, cpu.SP);
        Assert.Equal(2, cpu.Data[0x5a]);
        Assert.Equal(7, cpu.GetUint16(28));
        for (var index = 0; index < savedRegisters.Length; index++)
        {
            Assert.Equal(0xb0 + index, cpu.Data[savedRegisters[index]]);
        }
        Assert.Equal(0xdd, cpu.Data[0]);
    }

    [Theory]
    [InlineData(0x3f0006, 3)]
    [InlineData(0x3f0008, 4)]
    [InlineData(0x3f0010, 8)]
    [InlineData(0x3f0020, 16)]
    public void PrologueEntryEncodesSavedRegisterCount(int entry, int expectedCount)
    {
        var cpu = CreateCpu(entry, 0x123);
        cpu.SetUint16(28, 0x200);

        AsicRom.Dispatch(cpu);

        Assert.Equal(0x200 - expectedCount, cpu.GetUint16(28));
    }

    [Theory]
    [InlineData(0x3f0046, 0xb3, 5, 0x60)]
    [InlineData(0x3f0048, 0xb3, 3, 0x16)]
    [InlineData(0x3f0046, 0xff, 8, 0)]
    [InlineData(0x3f0048, 0xff, 8, 0)]
    public void EightBitShiftHelpersMatchCallSiteIdentities(int entry, int value, int shift, int expected)
    {
        var cpu = CreateCpu(entry, 0x123);
        cpu.Data[16] = (byte)value;
        cpu.Data[20] = (byte)shift;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, cpu.Data[16]);
    }

    [Theory]
    [InlineData(0x3f0050, 0x0001, 12, 0x1000)]
    [InlineData(0x3f0052, 0x7680, 8, 0x0076)]
    public void SixteenBitShiftHelpersMatchCallSiteIdentities(int entry, int value, int shift, int expected)
    {
        var cpu = CreateCpu(entry, 0x123);
        cpu.SetUint16(16, value);
        cpu.Data[20] = (byte)shift;

        AsicRom.Dispatch(cpu);

        Assert.Equal(expected, cpu.GetUint16(16));
    }

    [Fact]
    public void SixteenBitMultiplyReturnsLowHalf()
    {
        var cpu = CreateCpu(0x3f004a, 0x123);
        cpu.SetUint16(16, 300);
        cpu.SetUint16(20, 250);

        AsicRom.Dispatch(cpu);

        Assert.Equal(75000 & 0xffff, cpu.GetUint16(16));
    }

    [Fact]
    public void SixteenBitUnsignedDivisionReturnsQuotientAndRemainder()
    {
        var cpu = CreateCpu(0x3f0054, 0x123);
        cpu.SetUint16(16, 1234);
        cpu.SetUint16(20, 100);

        AsicRom.Dispatch(cpu);

        Assert.Equal(12, cpu.GetUint16(16));
        Assert.Equal(34, cpu.GetUint16(20));
    }

    [Fact]
    public void SixteenBitUnsignedDivisionMatchesRomZeroDivisorResult()
    {
        var cpu = CreateCpu(0x3f0054, 0x123);
        cpu.SetUint16(16, 0x0050);
        cpu.SetUint16(20, 0);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0xffff, cpu.GetUint16(16));
        Assert.Equal(0x0050, cpu.GetUint16(20));
    }

    [Fact]
    public void SixteenBitSignedDivisionReturnsQuotientAndRemainder()
    {
        var cpu = CreateCpu(0x3f0056, 0x123);
        cpu.SetUint16(16, unchecked((ushort)-1234));
        cpu.SetUint16(20, 100);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(-12, unchecked((short)cpu.GetUint16(16)));
        Assert.Equal(-34, unchecked((short)cpu.GetUint16(20)));
    }

    [Theory]
    [InlineData(0x3f005e, 1u, 25, 0x02000000u)]
    [InlineData(0x3f0060, 0x80000000u, 31, 1u)]
    public void ThirtyTwoBitShiftHelpersMatchCallSiteIdentities(int entry, uint value, int shift, uint expected)
    {
        var cpu = CreateCpu(entry, 0x123);
        SetUInt32(cpu, 16, value);
        cpu.Data[20] = (byte)shift;

        AsicRom.Dispatch(cpu);

        Assert.Equal(expected, GetUInt32(cpu, 16));
    }

    [Fact]
    public void ThirtyTwoBitMultiplyReturnsLowHalf()
    {
        var cpu = CreateCpu(0x3f005c, 0x123);
        SetUInt32(cpu, 16, 0x12345678);
        SetUInt32(cpu, 20, 0x1001);

        AsicRom.Dispatch(cpu);

        Assert.Equal(unchecked(0x12345678u * 0x1001u), GetUInt32(cpu, 16));
    }

    [Fact]
    public void ThirtyTwoBitUnsignedDivisionReturnsQuotientAndRemainder()
    {
        var cpu = CreateCpu(0x3f0062, 0x123);
        SetUInt32(cpu, 16, 0x12345678);
        SetUInt32(cpu, 20, 3);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345678u / 3, GetUInt32(cpu, 16));
        Assert.Equal(0x12345678u % 3, GetUInt32(cpu, 20));
    }

    [Fact]
    public void ThirtyTwoBitUnsignedDivisionMatchesRomZeroDivisorResult()
    {
        var cpu = CreateCpu(0x3f0062, 0x123);
        SetUInt32(cpu, 16, 0x12345678);
        SetUInt32(cpu, 20, 0);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(uint.MaxValue, GetUInt32(cpu, 16));
        Assert.Equal(0x12345678u, GetUInt32(cpu, 20));
    }

    [Fact]
    public void ThirtyTwoBitSignedDivisionReturnsQuotientAndRemainder()
    {
        var cpu = CreateCpu(0x3f0064, 0x123);
        SetUInt32(cpu, 16, unchecked((uint)-18));
        SetUInt32(cpu, 20, 17);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(unchecked((uint)-1), GetUInt32(cpu, 16));
        Assert.Equal(unchecked((uint)-1), GetUInt32(cpu, 20));
    }

    [Theory]
    [InlineData(0, 0x00000000u)]
    [InlineData(730, 0x44368000u)]
    [InlineData(-730, 0xc4368000u)]
    [InlineData(16777217, 0x4b800000u)]
    public void SignedIntegerToSingleMatchesRecoveredCompilerAbi(int value, uint expectedBits)
    {
        var cpu = CreateCpu(0x3f0088, 0x123);
        SetUInt32(cpu, 16, unchecked((uint)value));

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedBits, GetUInt32(cpu, 16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(0u, 0x00000000u)]
    [InlineData(730u, 0x44368000u)]
    [InlineData(uint.MaxValue, 0x4f800000u)]
    public void UnsignedIntegerToSingleMatchesRecoveredCompilerAbi(uint value, uint expectedBits)
    {
        var cpu = CreateCpu(0x3f008c, 0x123);
        SetUInt32(cpu, 16, value);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedBits, GetUInt32(cpu, 16));
    }

    [Theory]
    [InlineData(0x414c0000u, 12)]
    [InlineData(0xc14c0000u, -12)]
    [InlineData(0x00000000u, 0)]
    public void SingleToSignedIntegerTruncatesTowardZero(uint bits, int expected)
    {
        var cpu = CreateCpu(0x3f008e, 0x123);
        SetUInt32(cpu, 16, bits);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(unchecked((uint)expected), GetUInt32(cpu, 16));
    }

    [Theory]
    [InlineData(0x7fc00000u)]
    [InlineData(0x7f800000u)]
    [InlineData(0x4f000000u)]
    public void SingleToSignedIntegerFailsClosedForUnknownExceptionalResults(uint bits)
    {
        var cpu = CreateCpu(0x3f008e, 0x123);
        SetUInt32(cpu, 16, bits);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));

        Assert.Equal(0x3f008e, cpu.PC);
    }

    [Theory]
    [InlineData(0x3f0090, 0x3fc00000u, 0x40100000u, 0x40700000u)]
    [InlineData(0x3f0092, 0x40b00000u, 0x40000000u, 0x40600000u)]
    [InlineData(0x3f0094, 0xc0400000u, 0x3f000000u, 0xbfc00000u)]
    [InlineData(0x3f0096, 0x40e00000u, 0x40000000u, 0x40600000u)]
    [InlineData(0x3f0096, 0x3f800000u, 0x00000000u, 0x7f800000u)]
    public void SingleArithmeticMatchesIeee754CallChains(
        int entry,
        uint left,
        uint right,
        uint expected)
    {
        var cpu = CreateCpu(entry, 0x123);
        SetUInt32(cpu, 16, left);
        SetUInt32(cpu, 20, right);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, GetUInt32(cpu, 16));
    }

    [Theory]
    [InlineData(0xbf800000u, 0x00000000u, 0xaa, 0xab)]
    [InlineData(0x3f800000u, 0x3f800000u, 0xab, 0xaa)]
    [InlineData(0x40000000u, 0x3f800000u, 0xab, 0xaa)]
    public void SingleComparisonReturnsOrderingInCarryAndPreservesOtherFlags(
        uint left,
        uint right,
        int initialSreg,
        int expectedSreg)
    {
        var cpu = CreateCpu(0x3f0098, 0x123);
        SetUInt32(cpu, 16, left);
        SetUInt32(cpu, 20, right);
        cpu.Data[0x5f] = (byte)initialSreg;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedSreg, cpu.SREG);
    }

    [Fact]
    public void SingleComparisonFailsClosedForNanOrdering()
    {
        var cpu = CreateCpu(0x3f0098, 0x123);
        SetUInt32(cpu, 16, 0x7fc00000);
        SetUInt32(cpu, 20, 0);
        cpu.Data[0x5f] = 0xa5;

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Equal(0xa5, cpu.SREG);
    }

    [Theory]
    [InlineData(-5, 5)]
    [InlineData(7, 7)]
    [InlineData(short.MinValue, 0x8000)]
    public void SixteenBitAbsoluteValueMatchesSubtractionCallSites(int value, int expected)
    {
        var cpu = CreateCpu(0x3f00e4, 0x123);
        cpu.SetUint16(16, value);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, cpu.GetUint16(16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Fact]
    public void FarDataCopyConsumesBankedCountAndReadsThroughFlashBus()
    {
        const int physicalSource = 0x580000;
        const int logicalSource = 0xd80000;
        const int destination = 0xd6c000;
        const int stackPointer = 0xd6f000;
        var cpu = CreateCpu(0x3f009c, 0x123, 0x1000000);
        new byte[] { 0x1f, 0x00, 0x05, 0x00 }
            .CopyTo(cpu.ProgBytes, physicalSource);
        _ = new AsicFlashMemory(cpu);
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, logicalSource);
        cpu.SetUint16(28, stackPointer);
        cpu.Data[0x5a] = (byte)(stackPointer >> 16);
        cpu.SetUint16(stackPointer, 4);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x123, cpu.PC);
        Assert.Equal(logicalSource, GetUInt24(cpu, 20));
        Assert.Equal(destination, GetUInt24(cpu, 16));
        Assert.Equal((stackPointer + 2) & 0xffff, cpu.GetUint16(28));
        Assert.Equal((byte)((stackPointer + 2) >> 16), cpu.Data[0x5a]);
        Assert.Equal(new byte[] { 0x1f, 0x00, 0x05, 0x00 },
            cpu.Data[destination..(destination + 4)]);
    }

    [Theory]
    [InlineData("DisplayHWDriverType", "DisplayHWDriverType", 0)]
    [InlineData("A", "B", 0xffff)]
    [InlineData("B", "A", 1)]
    public void LogicalStringCompareReturnsSignedOrdering(string left, string right, int expected)
    {
        var cpu = CreateCpu(0x3f00aa, 0x123);
        const int leftAddress = 0x100;
        const int rightAddress = 0x200;
        System.Text.Encoding.ASCII.GetBytes(left + "\0").CopyTo(cpu.ProgBytes, leftAddress);
        System.Text.Encoding.ASCII.GetBytes(right + "\0").CopyTo(cpu.ProgBytes, rightAddress);
        SetUInt24(cpu, 16, leftAddress | 0x800000);
        SetUInt24(cpu, 20, rightAddress | 0x800000);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, cpu.GetUint16(16));
    }

    [Fact]
    public void StringCompareSupportsDataAndLogicalProgramOperands()
    {
        var cpu = CreateCpu(0x3f00aa, 0x123);
        const int dataAddress = 0x2000;
        const int physicalProgramAddress = 0x100;
        const int logicalProgramAddress = physicalProgramAddress | 0x800000;
        System.Text.Encoding.ASCII.GetBytes("interface\0").CopyTo(cpu.Data, dataAddress);
        System.Text.Encoding.ASCII.GetBytes("interface\0")
            .CopyTo(cpu.ProgBytes, physicalProgramAddress);
        SetUInt24(cpu, 16, dataAddress);
        SetUInt24(cpu, 20, logicalProgramAddress);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0, cpu.GetUint16(16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("DisplayHWDriverType", 19)]
    [InlineData("123456789", 9)]
    public void LogicalStringLengthReturnsSixteenBitLength(string value, int expected)
    {
        var cpu = CreateCpu(0x3f00b0, 0x123);
        const int address = 0x100;
        System.Text.Encoding.ASCII.GetBytes(value + "\0").CopyTo(cpu.ProgBytes, address);
        SetUInt24(cpu, 16, address | 0x800000);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, cpu.GetUint16(16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Fact]
    public void StringLengthReadsDataPointersFromTheDataBus()
    {
        const int address = 0x5049f;
        var cpu = CreateCpu(0x3f00b0, 0x123, 0x60000);
        System.Text.Encoding.ASCII.GetBytes("/\0").CopyTo(cpu.Data, address);
        System.Text.Encoding.ASCII.GetBytes("unrelated program string\0")
            .CopyTo(cpu.ProgBytes, address);
        SetUInt24(cpu, 16, address);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(1, cpu.GetUint16(16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false, '/', 8)]
    [InlineData(true, 'S', 1)]
    [InlineData(false, '$', -1)]
    [InlineData(true, '\0', 13)]
    public void LastStringByteSearchSupportsDataAndLogicalProgramSources(
        bool logicalSource,
        char sought,
        int expectedOffset)
    {
        var cpu = CreateCpu(0x3f00c0, 0x123);
        const int address = 0x200;
        var bytes = System.Text.Encoding.ASCII.GetBytes("FSX/path/leaf\0");
        if (logicalSource)
        {
            bytes.CopyTo(cpu.ProgBytes, address);
        }
        else
        {
            bytes.CopyTo(cpu.Data, address);
        }
        SetUInt24(cpu, 16, logicalSource ? address | 0x800000 : address);
        cpu.Data[20] = (byte)sought;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedOffset < 0 ? 0 :
            (logicalSource ? address | 0x800000 : address) + expectedOffset,
            GetUInt24(cpu, 16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringConcatenationSupportsDataAndLogicalProgramSources(bool logicalSource)
    {
        var cpu = CreateCpu(0x3f00a6, 0x123);
        const int destination = 0x300;
        const int source = 0x200;
        System.Text.Encoding.ASCII.GetBytes("/\0unused").CopyTo(cpu.Data, destination);
        var sourceBytes = System.Text.Encoding.ASCII.GetBytes("$\0");
        if (logicalSource)
        {
            sourceBytes.CopyTo(cpu.ProgBytes, source);
        }
        else
        {
            sourceBytes.CopyTo(cpu.Data, source);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, logicalSource ? source | 0x800000 : source);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal("/$\0", System.Text.Encoding.ASCII.GetString(
            cpu.Data[destination..(destination + 3)]));
        Assert.Equal(destination, GetUInt24(cpu, 16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedStringConcatenationSupportsDataAndLogicalProgramSources(
        bool logicalSource)
    {
        var cpu = CreateCpu(0x3f00bc, 0x123);
        const int destination = 0x300;
        const int source = 0x200;
        const int stack = 0x400;
        System.Text.Encoding.ASCII.GetBytes("cid:\0........").CopyTo(cpu.Data, destination);
        var sourceBytes = System.Text.Encoding.ASCII.GetBytes("abcdef\0");
        if (logicalSource)
        {
            sourceBytes.CopyTo(cpu.ProgBytes, source);
        }
        else
        {
            sourceBytes.CopyTo(cpu.Data, source);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, logicalSource ? source | 0x800000 : source);
        cpu.SetUint16(28, stack);
        cpu.SetUint16(stack, 3);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal("cid:abc\0.", System.Text.Encoding.ASCII.GetString(
            cpu.Data[destination..(destination + 9)]));
        Assert.Equal(destination, GetUInt24(cpu, 16));
        Assert.Equal(stack + 2, cpu.GetUint16(28));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData('R', -1)]
    [InlineData('-', 2)]
    [InlineData('\0', 4)]
    public void CharacterSetSearchReturnsLogicalPointerOrNull(char sought, int expectedOffset)
    {
        var cpu = CreateCpu(0x3f00a8, 0x123);
        const int address = 0x200;
        System.Text.Encoding.ASCII.GetBytes("/$-.\0").CopyTo(cpu.ProgBytes, address);
        SetUInt24(cpu, 16, address | 0x800000);
        cpu.Data[20] = (byte)sought;
        cpu.Data[21] = 0;
        cpu.Data[22] = 0xa5;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedOffset < 0 ? 0 : (address | 0x800000) + expectedOffset,
            GetUInt24(cpu, 16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringCopySupportsDataAndLogicalProgramSources(bool logicalSource)
    {
        var cpu = CreateCpu(0x3f00ac, 0x123);
        const int destination = 0x300;
        const int source = 0x200;
        var bytes = System.Text.Encoding.ASCII.GetBytes("FSX\0");
        if (logicalSource)
        {
            bytes.CopyTo(cpu.ProgBytes, source);
        }
        else
        {
            bytes.CopyTo(cpu.Data, source);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, logicalSource ? source | 0x800000 : source);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(bytes, cpu.Data[destination..(destination + bytes.Length)]);
        Assert.Equal(destination, GetUInt24(cpu, 16));
    }

    [Fact]
    public void StringCopyTreatsD6BankAsDataDespiteFlashAliasBit()
    {
        const int destination = 0x300;
        const int source = 0xd6c700;
        const int physicalRomAlias = source & 0x7fffff;
        var cpu = CreateCpu(0x3f00ac, 0x123, 0x1000000);
        System.Text.Encoding.ASCII.GetBytes("DATA\0").CopyTo(cpu.Data, source);
        System.Text.Encoding.ASCII.GetBytes("ROM\0").CopyTo(cpu.ProgBytes, physicalRomAlias);
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, source);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal("DATA\0", System.Text.Encoding.ASCII.GetString(
            cpu.Data[destination..(destination + 5)]));
        Assert.Equal(destination, GetUInt24(cpu, 16));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedStringCopyPadsShortDataAndLogicalProgramSources(bool logicalSource)
    {
        var cpu = CreateCpu(0x3f00b4, 0x123);
        const int destination = 0x300;
        const int source = 0x200;
        const int stack = 0x400;
        var sourceBytes = System.Text.Encoding.ASCII.GetBytes("FSX\0ignored");
        if (logicalSource)
        {
            sourceBytes.CopyTo(cpu.ProgBytes, source);
        }
        else
        {
            sourceBytes.CopyTo(cpu.Data, source);
        }
        Enumerable.Repeat((byte)0xa5, 8).ToArray().CopyTo(cpu.Data, destination);
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, logicalSource ? source | 0x800000 : source);
        cpu.SetUint16(28, stack);
        cpu.SetUint16(stack, 6);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(new byte[] { (byte)'F', (byte)'S', (byte)'X', 0, 0, 0, 0xa5, 0xa5 },
            cpu.Data[destination..(destination + 8)]);
        Assert.Equal(destination, GetUInt24(cpu, 16));
        Assert.Equal(stack + 2, cpu.GetUint16(28));
        Assert.Equal(0x123, cpu.PC);
    }

    [Fact]
    public void BoundedStringCopyDoesNotInventATerminatorAtTheLimit()
    {
        var cpu = CreateCpu(0x3f00b4, 0x123);
        const int destination = 0x300;
        const int source = 0x200;
        const int stack = 0x400;
        System.Text.Encoding.ASCII.GetBytes("ABCD\0").CopyTo(cpu.Data, source);
        Enumerable.Repeat((byte)0xa5, 5).ToArray().CopyTo(cpu.Data, destination);
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, source);
        cpu.SetUint16(28, stack);
        cpu.SetUint16(stack, 3);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(new byte[] { (byte)'A', (byte)'B', (byte)'C', 0xa5, 0xa5 },
            cpu.Data[destination..(destination + 5)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteCompareSupportsDataAndLogicalProgramOperands(bool equal)
    {
        var cpu = CreateCpu(0x3f009e, 0x123);
        const int left = 0x200;
        const int right = 0x300;
        const int stack = 0x400;
        new byte[] { 1, 2, 3, 4 }.CopyTo(cpu.ProgBytes, left);
        new byte[] { 1, 2, 3, equal ? (byte)4 : (byte)5 }.CopyTo(cpu.Data, right);
        SetUInt24(cpu, 16, 0x800000 | left);
        SetUInt24(cpu, 20, right);
        cpu.SetUint16(stack, 4);
        cpu.SetUint16(28, stack);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(equal ? 0 : -1, unchecked((short)cpu.GetUint16(16)));
        Assert.Equal(stack + 2, cpu.GetUint16(28));
    }

    [Fact]
    public void ByteCompareTreatsD6BankAsDataDespiteFlashAliasBit()
    {
        const int logical = 0xd13a08;
        const int physical = logical & 0x7fffff;
        const int data = 0xd6c7df;
        const int stack = 0xd6c7d0;
        var cpu = CreateCpu(0x3f009e, 0x123, 0x1000000);
        new byte[] { (byte)'A', (byte)'E', (byte)'S', (byte)'T' }.CopyTo(cpu.ProgBytes, physical);
        new byte[] { (byte)'A', (byte)'E', (byte)'S', (byte)'T' }.CopyTo(cpu.Data, data);
        SetUInt24(cpu, 16, logical);
        SetUInt24(cpu, 20, data);
        cpu.SetUint16(stack, 4);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = (byte)(stack >> 16);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0, cpu.GetUint16(16));
        Assert.Equal(stack + 2, cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
    }

    [Fact]
    public void ByteCompareDereferencesLogicalTaskWindowThroughSchedulerMapping()
    {
        const int flashPointer = 0xd13a08;
        const int logicalData = 0x02c81d;
        const int logicalStack = 0x02c81b;
        const int physicalData = 0x01c81d;
        const int physicalStack = 0x01c81b;
        var cpu = CreateCpu(0x3f009e, 0x123);
        cpu.ConfigureDataAddressWindow(0x02c700, 0x02c8ff, 0x01c700);
        new byte[] { 0, 0, 0, 0 }.CopyTo(
            cpu.ProgBytes,
            flashPointer & 0x7fffff);
        new byte[] { 0, 0, 0, 0 }.CopyTo(cpu.Data, physicalData);
        SetUInt24(cpu, 16, flashPointer);
        SetUInt24(cpu, 20, logicalData);
        cpu.SetUint16(physicalStack, 4);
        cpu.SetUint16(28, logicalStack);
        cpu.Data[0x5a] = 0x02;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0, cpu.GetUint16(16));
        Assert.Equal(logicalStack + 2, cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
    }

    [Theory]
    [InlineData(false, 'X', 4, 2)]
    [InlineData(true, 'p', 2, -1)]
    [InlineData(true, '\0', 8, 3)]
    public void ByteSearchHonorsItsBoundAndBothAddressSpaces(
        bool logicalSource,
        char sought,
        int count,
        int expectedOffset)
    {
        var cpu = CreateCpu(0x3f00a0, 0x123);
        const int address = 0x200;
        const int stack = 0x400;
        var bytes = System.Text.Encoding.ASCII.GetBytes("FSX\0path");
        if (logicalSource)
        {
            bytes.CopyTo(cpu.ProgBytes, address);
        }
        else
        {
            bytes.CopyTo(cpu.Data, address);
        }
        SetUInt24(cpu, 16, logicalSource ? address | 0x800000 : address);
        cpu.Data[20] = (byte)sought;
        cpu.SetUint16(28, stack);
        cpu.SetUint16(stack, count);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedOffset < 0 ? 0 :
            (logicalSource ? address | 0x800000 : address) + expectedOffset,
            GetUInt24(cpu, 16));
        Assert.Equal(stack + 2, cpu.GetUint16(28));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 3)]
    public void RejectedStringPrefixLengthSupportsBothAddressSpaces(
        bool logicalValue,
        bool logicalRejected,
        int expected)
    {
        var cpu = CreateCpu(0x3f00ae, 0x123);
        const int valueAddress = 0x200;
        const int rejectedAddress = 0x300;
        var value = System.Text.Encoding.ASCII.GetBytes("FSX/path\0");
        var rejected = System.Text.Encoding.ASCII.GetBytes("/$\0");
        value.CopyTo(logicalValue ? cpu.ProgBytes : cpu.Data, valueAddress);
        rejected.CopyTo(logicalRejected ? cpu.ProgBytes : cpu.Data, rejectedAddress);
        SetUInt24(cpu, 16, logicalValue ? valueAddress | 0x800000 : valueAddress);
        SetUInt24(cpu, 20, logicalRejected ? rejectedAddress | 0x800000 : rejectedAddress);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, cpu.GetUint16(16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public void BoundedStringCompareHonorsItsCount(int count, int expected)
    {
        var cpu = CreateCpu(0x3f00b2, 0x123);
        const int leftAddress = 0x200;
        const int rightAddress = 0x300;
        const int stack = 0x400;
        System.Text.Encoding.ASCII.GetBytes("FSX/path\0").CopyTo(cpu.Data, leftAddress);
        System.Text.Encoding.ASCII.GetBytes("FSX-other\0").CopyTo(cpu.ProgBytes, rightAddress);
        SetUInt24(cpu, 16, leftAddress);
        SetUInt24(cpu, 20, rightAddress | 0x800000);
        cpu.SetUint16(28, stack);
        cpu.SetUint16(stack, count);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, unchecked((short)cpu.GetUint16(16)));
        Assert.Equal(stack + 2, cpu.GetUint16(28));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AcceptedStringPrefixLengthSupportsBothAddressSpaces(
        bool logicalValue,
        bool logicalAccepted)
    {
        var cpu = CreateCpu(0x3f00b6, 0x123);
        const int valueAddress = 0x200;
        const int acceptedAddress = 0x300;
        var value = System.Text.Encoding.ASCII.GetBytes("ABC123\0");
        var accepted = System.Text.Encoding.ASCII.GetBytes("CBA\0");
        value.CopyTo(logicalValue ? cpu.ProgBytes : cpu.Data, valueAddress);
        accepted.CopyTo(logicalAccepted ? cpu.ProgBytes : cpu.Data, acceptedAddress);
        SetUInt24(cpu, 16, logicalValue ? valueAddress | 0x800000 : valueAddress);
        SetUInt24(cpu, 20, logicalAccepted ? acceptedAddress | 0x800000 : acceptedAddress);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(3, cpu.GetUint16(16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SubstringSearchSupportsBothAddressSpaces(
        bool logicalHaystack,
        bool logicalNeedle)
    {
        var cpu = CreateCpu(0x3f00c2, 0x123);
        const int haystackAddress = 0x200;
        const int needleAddress = 0x300;
        var haystack = System.Text.Encoding.ASCII.GetBytes("Sony Ericsson\0");
        var needle = System.Text.Encoding.ASCII.GetBytes("Eric\0");
        haystack.CopyTo(logicalHaystack ? cpu.ProgBytes : cpu.Data, haystackAddress);
        needle.CopyTo(logicalNeedle ? cpu.ProgBytes : cpu.Data, needleAddress);
        SetUInt24(cpu, 16, logicalHaystack ? haystackAddress | 0x800000 : haystackAddress);
        SetUInt24(cpu, 20, logicalNeedle ? needleAddress | 0x800000 : needleAddress);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal((logicalHaystack ? haystackAddress | 0x800000 : haystackAddress) + 5,
            GetUInt24(cpu, 16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData("Motorola", -1)]
    [InlineData("", 0)]
    public void SubstringSearchReturnsNullOrTheHaystackForEdgeCases(
        string needleValue,
        int expectedOffset)
    {
        var cpu = CreateCpu(0x3f00c2, 0x123);
        const int haystackAddress = 0x200;
        const int needleAddress = 0x300;
        System.Text.Encoding.ASCII.GetBytes("Sony Ericsson\0").CopyTo(cpu.Data, haystackAddress);
        System.Text.Encoding.ASCII.GetBytes(needleValue + "\0").CopyTo(cpu.Data, needleAddress);
        SetUInt24(cpu, 16, haystackAddress);
        SetUInt24(cpu, 20, needleAddress);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expectedOffset < 0 ? 0 : haystackAddress + expectedOffset,
            GetUInt24(cpu, 16));
    }

    [Theory]
    [InlineData(false, "123456tail", 123456u)]
    [InlineData(true, " \t-2147483648", 0x80000000u)]
    [InlineData(true, "+42", 42u)]
    public void LongConversionParsesDataAndLogicalProgramStrings(
        bool logicalSource,
        string text,
        uint expected)
    {
        var cpu = CreateCpu(0x3f00e8, 0x123);
        const int address = 0x200;
        var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        bytes.CopyTo(logicalSource ? cpu.ProgBytes : cpu.Data, address);
        SetUInt24(cpu, 16, logicalSource ? address | 0x800000 : address);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, GetUInt32(cpu, 16));
        Assert.Equal(0x123, cpu.PC);
    }

    [Theory]
    [InlineData(false, "+200", 200)]
    [InlineData(true, "\r\n-12x", -12)]
    [InlineData(false, "not a number", 0)]
    public void IntConversionParsesDataAndLogicalProgramStrings(
        bool logicalSource,
        string text,
        int expected)
    {
        var cpu = CreateCpu(0x3f00ea, 0x123);
        const int address = 0x200;
        var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        bytes.CopyTo(logicalSource ? cpu.ProgBytes : cpu.Data, address);
        SetUInt24(cpu, 16, logicalSource ? address | 0x800000 : address);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(expected, unchecked((short)cpu.GetUint16(16)));
        Assert.Equal(0x123, cpu.PC);
    }

    [Fact]
    public void SchedulerInitializesAndRestoresFirmwarePrebuiltTaskFrame()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int taskEntry = 0x13456;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        SetUInt24(cpu, descriptor + 22, taskEntry);
        cpu.Data[descriptor + 5] = 0x19;
        cpu.Data[descriptor + 11] = 0x1e;
        cpu.SetUint16(descriptor + 16, 0x2300);
        cpu.Data[descriptor + 25] = 0x20;
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x022ffe, 0x021ffe));

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(0, cpu.GetUint16(context - 2));
        Assert.Equal(0xfdfd, cpu.GetUint16(context));
        Assert.Equal(descriptor, cpu.GetUint16(context + 12));
        Assert.Equal(0x2300, cpu.GetUint16(context + 16));
        Assert.Equal(context - 14, cpu.GetUint16(context + 18));
        Assert.Equal(0x19, cpu.Data[context + 5]);
        Assert.Equal(0x1e, cpu.Data[context + 11]);
        Assert.Equal(0x20, cpu.Data[context + 25]);
        cpu.SetUint16(context - 4, 0xfdfd);
        var contextInfo = Assert.Single(AsicRom.GetSchedulerContexts(cpu));
        Assert.Equal(descriptor, contextInfo.DescriptorAddress);
        Assert.Equal(0x19, contextInfo.Process);
        Assert.Equal(taskEntry, contextInfo.TaskEntry);
        Assert.Equal(0x00221b, contextInfo.SavedPc);
        Assert.Equal(context - 5, contextInfo.SavedStackPointer);
        Assert.Equal(0x002ffe, contextInfo.SoftwareStackPointer);
        Assert.Equal(32, contextInfo.Registers.Length);
        Assert.Equal(context - 4, contextInfo.HardwareStackStart);
        Assert.Equal(
            new byte[] { (byte)(descriptor & 0xff), (byte)(descriptor >> 8) },
            contextInfo.HardwareStack.ToArray());
        Assert.Equal(0, contextInfo.State);
        Assert.False(contextInfo.Runnable);

        cpu.PC = 0x3f015e;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x00221b, cpu.PC);
        Assert.Equal(context - 5, cpu.SP);
        Assert.Equal(0x2ffe, cpu.GetUint16(28));
        Assert.Equal(0x00, cpu.Data[0x5a]);
        Assert.Equal(0x80, cpu.SREG);
    }

    [Fact]
    public void SchedulerSeparatesSoftwareStackWindowFromHardwareStackAllocation()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x0190ff, 0x019000));

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.PC = 0x3f015e;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x90ff, cpu.GetUint16(28));
        Assert.Equal(0, cpu.Data[0x5a]);
        Assert.Equal(0x019000, cpu.TranslateDataAddress(0x029000));
        Assert.Equal(0x021f00, cpu.TranslateDataAddress(0x021f00));
    }

    [Fact]
    public void SchedulerRejectsContextOutsideDescriptorHardwareStackAllocation()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        cpu.SetUint16(descriptor + 31, context - 3);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Empty(AsicRom.GetSchedulerContexts(cpu));
    }

    [Fact]
    public void SchedulerFailsClosedForUnrecoveredTypeOneContextContract()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 10] = 1;
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Empty(AsicRom.GetSchedulerContexts(cpu));
    }

    [Fact]
    public void SchedulerRejectsContextWhoseFrameNamesAnotherDescriptor()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor + 1);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Empty(AsicRom.GetSchedulerContexts(cpu));
    }

    [Fact]
    public void SchedulerRepeatedPrepareOnlyValidatesImmutableDescriptor()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x50;
        SetUInt24(cpu, descriptor + 22, 0xcc4f9);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.Data[context + 4] = 4;
        cpu.Data[context + 14] = 0x61;
        cpu.Data[descriptor + 4] = 1;
        cpu.Data[descriptor + 14] = 0x62;
        var contextBefore = cpu.Data.AsSpan(context, 26).ToArray();
        var descriptorBefore = cpu.Data.AsSpan(descriptor, 40).ToArray();
        PushRomCall(cpu, 0x3f015c, 0x23456);
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x23456, cpu.PC);
        Assert.Equal(contextBefore, cpu.Data.AsSpan(context, 26).ToArray());
        Assert.Equal(descriptorBefore, cpu.Data.AsSpan(descriptor, 40).ToArray());

        cpu.Data[descriptor + 10] = 1;
        PushRomCall(cpu, 0x3f015c, 0x34567);
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
    }

    [Fact]
    public void SchedulerInitializationRejectsMalformedPendingSignalQueue()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int pendingHead = 0x2400;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        cpu.SetUint16(descriptor, pendingHead);
        cpu.SetUint16(descriptor + 2, 0);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Empty(AsicRom.GetSchedulerContexts(cpu));
    }

    [Fact]
    public void SchedulerRestoreRejectsMalformedActiveSignalQueueBeforeSplice()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int contextHead = 0x2400;
        const int pendingHead = 0x2500;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(context, contextHead);
        cpu.SetUint16(context + 2, 0);
        cpu.SetUint16(descriptor, pendingHead);
        cpu.SetUint16(descriptor + 2, pendingHead);
        cpu.SetUint16(pendingHead, 0xfdfd);
        cpu.PC = 0x3f015e;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Equal(contextHead, cpu.GetUint16(context));
        Assert.Equal(pendingHead, cpu.GetUint16(descriptor));
    }

    [Fact]
    public void SchedulerInitializationPreservesSignalsPostedBeforeFirstTaskRestore()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int signalNode = 0x2400;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.SetUint16(descriptor, signalNode);
        cpu.SetUint16(descriptor + 2, signalNode);
        cpu.SetUint16(signalNode, 0x00fd);
        cpu.Data[descriptor + 5] = 0x50;
        SetUInt24(cpu, descriptor + 22, 0xcc4f9);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(signalNode, cpu.GetUint16(context));
        Assert.Equal(signalNode, cpu.GetUint16(context + 2));
        Assert.Equal(0xfdfd, cpu.GetUint16(descriptor));
        Assert.Equal(descriptor, cpu.GetUint16(descriptor + 2));
    }

    [Fact]
    public void SchedulerAppendsInactiveSignalsAndAdvancesContextQueueTail()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int existingNode = 0x2400;
        const int pendingHead = 0x2500;
        const int pendingTail = 0x2600;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x50;
        SetUInt24(cpu, descriptor + 22, 0xcc4f9);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(context, existingNode);
        cpu.SetUint16(context + 2, existingNode);
        cpu.SetUint16(existingNode, 0xfdfd);
        cpu.SetUint16(descriptor, pendingHead);
        cpu.SetUint16(descriptor + 2, pendingTail);
        cpu.SetUint16(pendingHead, pendingTail);
        cpu.SetUint16(pendingTail, 0xfdfd);
        cpu.PC = 0x3f015e;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(existingNode, cpu.GetUint16(context));
        Assert.Equal(pendingHead, cpu.GetUint16(existingNode));
        Assert.Equal(pendingTail, cpu.GetUint16(context + 2));
        Assert.Equal(0xfdfd, cpu.GetUint16(pendingTail));
        Assert.Equal(0xfdfd, cpu.GetUint16(descriptor));
        Assert.Equal(descriptor, cpu.GetUint16(descriptor + 2));
    }

    [Fact]
    public void SchedulerPublishesEveryInitializedSoftwareStackWindow()
    {
        const int receiverContext = 0x2000;
        const int unrelatedContext = 0x2100;
        const int senderContext = 0x2200;
        const int receiverDescriptor = receiverContext + 31;
        const int unrelatedDescriptor = unrelatedContext + 31;
        const int senderDescriptor = senderContext + 31;
        const byte receiverProcess = 0x21;
        const byte unrelatedProcess = 0x1a;
        const byte senderProcess = 0x62;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        var firstContext = true;

        void Initialize(
            int context,
            int descriptor,
            byte process,
            int physicalStackFirst)
        {
            if (!firstContext)
            {
                PushRomCall(cpu, 0x3f015c, 0x12345);
            }
            firstContext = false;
            SetZ(cpu, context);
            cpu.SetUint16(context - 4, descriptor);
            cpu.SetUint16(descriptor, 0xfdfd);
            cpu.SetUint16(descriptor + 2, descriptor);
            cpu.Data[descriptor + 5] = process;
            SetUInt24(cpu, descriptor + 22, 0x1234);
            SetSoftwareStackBounds(cpu, new(
                context,
                descriptor,
                physicalStackFirst + 0xff,
                physicalStackFirst));
            Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        }

        Initialize(receiverContext, receiverDescriptor, receiverProcess, 0x019000);
        Initialize(unrelatedContext, unrelatedDescriptor, unrelatedProcess, 0x01a000);
        Initialize(senderContext, senderDescriptor, senderProcess, 0x018000);

        cpu.PC = 0x3f015e;
        SetZ(cpu, receiverContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x019020, cpu.TranslateDataAddress(0x029020));
        Assert.Equal(0x018020, cpu.TranslateDataAddress(0x028020));

        cpu.PC = 0x3f015e;
        SetZ(cpu, unrelatedContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x01a020, cpu.TranslateDataAddress(0x02a020));
        Assert.Equal(0x018020, cpu.TranslateDataAddress(0x028020));

        // ROM context initialization publishes each non-overlapping aperture;
        // task switches change priority, not the mapping's lifetime.
        cpu.PC = 0x3f015e;
        SetZ(cpu, senderContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x018020, cpu.TranslateDataAddress(0x028020));

        cpu.PC = 0x3f015e;
        SetZ(cpu, receiverContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x018020, cpu.TranslateDataAddress(0x028020));
    }


    [Fact]
    public void ProcessDescriptorInventoryUsesFirmwareProcessTable()
    {
        const int descriptorTable = 0xf3ba;
        const int descriptor = 0x2200;
        const int taskEntry = 0x4c460;
        var cpu = CreateCpu(0x3f0078, 0x12345);
        cpu.SetUint16(descriptorTable + 0x36 * 2, descriptor);
        cpu.Data[descriptor] = 0xfd;
        cpu.Data[descriptor + 1] = 0xfd;
        cpu.Data[descriptor + 5] = 0x36;
        SetUInt24(cpu, descriptor + 22, taskEntry);

        var info = Assert.Single(AsicRom.GetProcessDescriptors(cpu));

        Assert.Equal(0x36, info.Process);
        Assert.Equal(descriptor, info.Address);
        Assert.Equal(taskEntry, info.TaskEntry);
        Assert.Equal(0, info.State);
    }

    [Theory]
    [InlineData(0x08, 0x08, 0x00, 0x63)]
    [InlineData(0x08, 0x08, 0x63, 0x00)]
    [InlineData(0x04, 0x00, 0x00, 0x63)]
    [InlineData(0x04, 0x00, 0x63, 0x00)]
    public void SchedulerSavePreservesTimerLinksAndRestoreLeavesRunnableBitToFirmware(
        byte contextState,
        byte expectedDescriptorState,
        byte timerNext,
        byte staleTimerNext)
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int savedSp = 0x1fd0;
        const int schedulerReturn = 0x2284;
        const int resumePc = 0x23456;
        const int processBitmap = 0x2300;
        Cpu cpu = CreateSchedulerSaveScenario(
            contextState,
            timerNext,
            staleTimerNext);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(schedulerReturn, cpu.PC);
        Assert.Equal(savedSp, cpu.SP);
        Assert.Equal(0x20, cpu.Data[processBitmap]);
        Assert.Equal(expectedDescriptorState, cpu.Data[descriptor + 4]);
        Assert.Equal(timerNext, cpu.Data[descriptor + 14]);
        Assert.Equal(0x45, cpu.Data[descriptor + 15]);

        // The native timer tick wakes a delayed task by changing the static
        // descriptor state. The mask-ROM restore must move that state to the
        // active context before the firmware resumes and tests context +4.
        cpu.Data[descriptor + 4] = 0x01;
        cpu.Data[descriptor + 14] = 0x62;
        cpu.Data[descriptor + 15] = 0x61;
        cpu.Data[savedSp + 10] = 0;
        cpu.Data[context - 3] = 0;

        cpu.SetStatusRegister(0x25);
        bool? restoredInterruptEnable = null;
        cpu.GlobalInterruptEnableChanged += (_, args) =>
            restoredInterruptEnable = args.Enabled;
        cpu.PC = 0x3f015e;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(resumePc, cpu.PC);
        Assert.Equal(0x20, cpu.Data[processBitmap]);
        Assert.Equal(savedSp + 9, cpu.SP);
        Assert.Equal(0xa4, cpu.Data[4]);
        Assert.Equal(0x66, cpu.Data[16]);
        Assert.Equal(0x77, cpu.Data[17]);
        Assert.Equal(0x1ff0, cpu.GetUint16(28));
        Assert.Equal(0x9a, cpu.Data[30]);
        Assert.Equal(0xbc, cpu.Data[31]);
        Assert.Equal(1, cpu.Data[0x58]);
        Assert.Equal(2, cpu.Data[0x59]);
        Assert.Equal(2, cpu.Data[0x5a]);
        Assert.Equal(3, cpu.Data[0x5b]);
        Assert.Equal(5, cpu.Data[0x5c]);
        Assert.Equal(0xa5, cpu.SREG);
        Assert.True(restoredInterruptEnable);
        Assert.Equal(0x01, cpu.Data[context + 4]);
        Assert.Equal(0x62, cpu.Data[context + 14]);
        Assert.Equal(0x61, cpu.Data[context + 15]);
        Assert.Equal(0x00, cpu.Data[descriptor + 4]);
        Assert.Equal(0xaa, cpu.Data[savedSp + 10]);
        Assert.Equal(0xbb, cpu.Data[context - 3]);
    }

    static Cpu CreateSchedulerSaveScenario(
        byte contextState,
        byte timerNext,
        byte staleTimerNext)
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int savedSp = 0x1fd0;
        const int schedulerReturn = 0x2284;
        const int resumePc = 0x23456;
        const int processBitmap = 0x2300;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.SetUint16(descriptor + 16, processBitmap);
        cpu.Data[descriptor + 25] = 0x20;
        cpu.Data[processBitmap] = 0x20;
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        AsicRom.Dispatch(cpu);
        cpu.PC = 0x3f015e;
        SetZ(cpu, context);
        AsicRom.Dispatch(cpu);
        // Native receive/delay paths clear this bit when they block; selecting
        // or restoring a task does not consume it.
        Assert.Equal(0x20, cpu.Data[processBitmap]);

        cpu.Data[4] = 0xa4;
        cpu.SetUint16(28, 0x1ff0);
        cpu.Data[0x58] = 1;
        cpu.Data[0x59] = 2;
        cpu.Data[0x5a] = 2;
        cpu.Data[0x5c] = 5;
        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 1] = 3;
        // The firmware save wrapper strips SREG.I before storing this byte;
        // mask-ROM task restore is responsible for enabling interrupts again.
        cpu.Data[savedSp + 2] = 0x25;
        cpu.Data[savedSp + 3] = 0x66;
        cpu.Data[savedSp + 4] = 0x77;
        cpu.Data[savedSp + 5] = 0x9a;
        cpu.Data[savedSp + 6] = 0xbc;
        cpu.Data[savedSp + 7] = (byte)(resumePc >> 16 & 0xff);
        cpu.Data[savedSp + 8] = (byte)(resumePc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(resumePc & 0xff);
        cpu.Data[savedSp - 2] = (byte)(schedulerReturn >> 16 & 0xff);
        cpu.Data[savedSp - 1] = (byte)(schedulerReturn >> 8 & 0xff);
        cpu.Data[savedSp] = (byte)(schedulerReturn & 0xff);
        cpu.Data[savedSp + 10] = 0xaa;
        cpu.Data[context - 3] = 0xbb;
        cpu.Data[context + 4] = contextState;
        cpu.Data[context + 14] = timerNext;
        cpu.Data[context + 15] = 0x45;
        cpu.Data[descriptor + 4] = 0x00;
        cpu.Data[descriptor + 14] = staleTimerNext;
        cpu.Data[descriptor + 15] = 0x44;
        cpu.SP = savedSp - 3;
        cpu.PC = 0x3f0160;
        SetZ(cpu, context);
        return cpu;
    }

    [Fact]
    public void SchedulerSavePublishesPendingReceiveWakeForNativeRestore()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int pendingSignal = 0x2400;
        const int savedSp = 0x1fd0;
        const int schedulerReturn = 0x2284;
        const int resumePc = 0x23456;
        const int processBitmap = 0x2300;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.SetUint16(descriptor + 16, processBitmap);
        cpu.Data[descriptor + 25] = 0x20;
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.PC = 0x3f015e;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        // A self-send while this task is active lands on its static descriptor.
        // The receive wrapper has already missed it in the live context queue
        // and clears the runnable bit immediately before saving state 4.
        cpu.SetUint16(descriptor, pendingSignal);
        cpu.SetUint16(descriptor + 2, pendingSignal);
        cpu.SetUint16(pendingSignal, 0xfdfd);
        cpu.Data[processBitmap] = 0;
        cpu.Data[context + 4] = 4;
        cpu.Data[descriptor + 4] = 0;

        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 1] = 0;
        cpu.Data[savedSp + 2] = 0x25;
        cpu.Data[savedSp + 7] = (byte)(resumePc >> 16);
        cpu.Data[savedSp + 8] = (byte)(resumePc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(resumePc & 0xff);
        cpu.Data[savedSp - 2] = (byte)(schedulerReturn >> 16);
        cpu.Data[savedSp - 1] = (byte)(schedulerReturn >> 8);
        cpu.Data[savedSp] = (byte)(schedulerReturn & 0xff);
        cpu.SP = savedSp - 3;
        cpu.PC = 0x3f0160;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x00, cpu.Data[descriptor + 4]);
        Assert.Equal(0x20, cpu.Data[processBitmap]);
        Assert.Equal(pendingSignal, cpu.GetUint16(descriptor));
        Assert.Equal(pendingSignal, cpu.GetUint16(descriptor + 2));

        cpu.PC = 0x3f015e;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(pendingSignal, cpu.GetUint16(context));
        Assert.Equal(pendingSignal, cpu.GetUint16(context + 2));
        Assert.Equal(0xfdfd, cpu.GetUint16(descriptor));
        Assert.Equal(descriptor, cpu.GetUint16(descriptor + 2));
        Assert.Equal(0x00, cpu.Data[context + 4]);
        Assert.Equal(0x00, cpu.Data[descriptor + 4]);
    }

    [Theory]
    [InlineData(0x1eff)]
    [InlineData(0x1ff8)]
    public void SchedulerSaveRejectsFrameOutsideFirmwareHardwareStackAllocation(
        int savedStackPointer)
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        SetUInt24(cpu, descriptor + 22, 0x13456);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        var initialContext = Assert.Single(AsicRom.GetSchedulerContexts(cpu));

        cpu.SetUint16(context + 18, savedStackPointer);
        cpu.PC = 0x3f0160;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        var retainedContext = Assert.Single(AsicRom.GetSchedulerContexts(cpu));
        Assert.Equal(initialContext.SavedPc, retainedContext.SavedPc);
        Assert.Equal(initialContext.SavedStackPointer, retainedContext.SavedStackPointer);
    }

    [Theory]
    [InlineData(false, false, 4)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 1)]
    public void InterruptSavePublishesLiveStateAndDirectRestoreTransfersPendingSignals(
        bool wakeDuringInterrupt,
        bool signalDuringInterrupt,
        byte expectedContextState)
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0x1fd0;
        const int signalNode = 0x2400;
        Cpu cpu = CreateInterruptSaveScenario();
        PublishInterruptChanges(cpu, wakeDuringInterrupt, signalDuringInterrupt);

        Array.Clear(cpu.Data, 0, 32);
        cpu.Data[interruptedSp + 1] = 0;
        cpu.PC = 0x3f0162;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(interruptedPc, cpu.PC);
        Assert.Equal(interruptedSp, cpu.SP);
        Assert.Equal(0x66, cpu.Data[16]);
        Assert.Equal(0x77, cpu.Data[17]);
        Assert.Equal(0x9a, cpu.Data[30]);
        Assert.Equal(0xbc, cpu.Data[31]);
        Assert.Equal(2, cpu.Data[0x59]);
        Assert.Equal(3, cpu.Data[0x5a]);
        Assert.Equal(3, cpu.Data[0x5b]);
        Assert.Equal(5, cpu.Data[0x5c]);
        Assert.Equal(0x95, cpu.SREG);
        Assert.Equal(0xbb, cpu.Data[interruptedSp + 1]);
        Assert.Equal(expectedContextState, cpu.Data[context + 4]);
        Assert.Equal(0, cpu.Data[context + 14]);
        Assert.Equal(0, cpu.Data[context + 15]);
        Assert.Equal(0, cpu.Data[descriptor + 4]);
        Assert.Equal(0, cpu.Data[descriptor + 14]);
        Assert.Equal(0, cpu.Data[descriptor + 15]);
        Assert.Equal(
            signalDuringInterrupt ? signalNode : 0xfdfd,
            cpu.GetUint16(context));
        Assert.Equal(
            signalDuringInterrupt ? signalNode : 0,
            cpu.GetUint16(context + 2));
        Assert.Equal(signalDuringInterrupt ? 0xff : 0, cpu.Data[context + 39]);
        Assert.Equal(0xfdfd, cpu.GetUint16(descriptor));
        Assert.Equal(descriptor, cpu.GetUint16(descriptor + 2));
    }

    static Cpu CreateInterruptSaveScenario()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0x1fd0;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x71;
        SetUInt24(cpu, descriptor + 22, 0x0ca573);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(0xf608, context);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        cpu.Data[16] = 0x66;
        cpu.Data[17] = 0x77;
        cpu.Data[30] = 0x9a;
        cpu.Data[31] = 0xbc;
        cpu.Data[0x59] = 2;
        cpu.Data[0x5a] = 3;
        cpu.Data[0x5b] = 3;
        cpu.Data[0x5c] = 5;
        cpu.Data[0x5f] = 0x95;
        cpu.Data[interruptedSp + 1] = 0xbb;
        cpu.Data[context + 4] = 4;
        cpu.Data[context + 14] = 0;
        cpu.Data[context + 15] = 0;
        cpu.Data[descriptor + 4] = 0;
        cpu.Data[descriptor + 14] = 0x67;
        cpu.Data[descriptor + 15] = 0x1f;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));
        return cpu;
    }

    static void PublishInterruptChanges(
        Cpu cpu,
        bool wakeDuringInterrupt,
        bool signalDuringInterrupt)
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0x1fd0;
        const int signalNode = 0x2400;
        var savedSp = interruptedSp - 9;
        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 1] = 3;
        cpu.Data[savedSp + 2] = 0x15;
        cpu.Data[savedSp + 3] = 0x66;
        cpu.Data[savedSp + 4] = 0x77;
        cpu.Data[savedSp + 5] = 0x9a;
        cpu.Data[savedSp + 6] = 0xbc;
        cpu.Data[savedSp + 7] = (byte)(interruptedPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(interruptedPc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(interruptedPc & 0xff);

        // The firmware switches to its shared stack before calling 0x0164.
        // Model that call overwriting the same bytes which held the task's
        // outer return addresses in data RAM.
        cpu.Data[interruptedSp + 1] = 0x11;
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0, cpu.Data[0x59]);
        Assert.Equal(0, cpu.Data[0x5a]);
        Assert.Equal(0, cpu.Data[0x5c]);
        Assert.Equal(0, cpu.Data[descriptor + 14]);
        Assert.Equal(0, cpu.Data[descriptor + 15]);

        // A wake can race the direct interrupt unwind after the active task's
        // context was captured. It is published through the static descriptor.
        // A zero descriptor remains non-authoritative, while a nonzero wake is
        // merged into the live context without importing stale timer links.
        if (wakeDuringInterrupt)
        {
            cpu.Data[descriptor + 4] = 1;
        }
        if (signalDuringInterrupt)
        {
            cpu.SetUint16(descriptor, signalNode);
            cpu.SetUint16(descriptor + 2, signalNode);
            cpu.SetUint16(signalNode, 0xfdfd);
        }
    }

    [Fact]
    public void InterruptSaveAcceptsFrameBytesClobberedBySharedRomCall()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int dispatcherContext = 0xf7ab;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0xf5fe;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x39;
        SetUInt24(cpu, descriptor + 22, 0x0b90ea);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(0xf608, context);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        cpu.Data[16] = 0x66;
        cpu.Data[17] = 0x77;
        cpu.Data[30] = 0x9a;
        cpu.Data[31] = 0xbc;
        cpu.Data[0x5b] = 3;
        cpu.Data[0x5f] = 0x95;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = interruptedSp - 9;
        cpu.SetUint16(dispatcherContext + 18, savedSp);
        cpu.Data[savedSp + 1] = 3;
        cpu.Data[savedSp + 2] = 0x15;
        cpu.Data[savedSp + 3] = 0x66;
        cpu.Data[savedSp + 4] = 0x77;
        cpu.Data[savedSp + 5] = 0x9a;
        cpu.Data[savedSp + 6] = 0xbc;
        cpu.Data[savedSp + 7] = (byte)(interruptedPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(interruptedPc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(interruptedPc & 0xff);

        // Once the wrapper switches SP back to 0xf5fe, its three register
        // pushes and three-byte CALL return address overwrite f5f9..f5fe.
        // At the top of the task stack that includes half of the visible
        // interrupt frame, so mask ROM must rely on the edge-captured copy.
        cpu.Data[0xf5f9] = 0x00;
        cpu.Data[0xf5fa] = 0x16;
        cpu.Data[0xf5fb] = 0x6b;
        cpu.Data[0xf5fc] = 0xf9;
        cpu.Data[0xf5fd] = 0xc9;
        cpu.Data[0xf5fe] = 0x39;
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, dispatcherContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Array.Clear(cpu.Data, 0, 32);
        cpu.PC = 0x3f0162;
        SetZ(cpu, dispatcherContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(interruptedPc, cpu.PC);
        Assert.Equal(interruptedSp, cpu.SP);
        Assert.Equal(0x66, cpu.Data[16]);
        Assert.Equal(0x77, cpu.Data[17]);
        Assert.Equal(0x9a, cpu.Data[30]);
        Assert.Equal(0xbc, cpu.Data[31]);
        Assert.Equal(3, cpu.Data[0x5b]);
        Assert.Equal(0x95, cpu.SREG);
    }

    [Fact]
    public void StringWalkerCallsFirmwareCallbackForEachByteAndConsumesItsArguments()
    {
        const int source = 0x1234;
        const int callback = 0x73512;
        const int callbackContext = 0x24567;
        const int argumentCursorAddress = 0x2100;
        const int arguments = 0x2200;
        var cpu = CreateCpu(0x3f0122, 0x23456);
        cpu.ProgBytes[source] = (byte)'A';
        cpu.ProgBytes[source + 1] = (byte)'B';
        cpu.ProgBytes[source + 2] = 0;
        cpu.SetUint16(28, 0x2000);
        SetUInt24(cpu, 16, 0x800000 | source);
        SetUInt24(cpu, 20, callback);
        SetUInt24(cpu, 0x2000, callbackContext);
        SetUInt24(cpu, 0x2003, argumentCursorAddress);
        SetUInt24(cpu, argumentCursorAddress, arguments);
        var originalSp = cpu.SP;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(callback, cpu.PC);
        Assert.Equal((byte)'A', cpu.Data[16]);
        Assert.Equal(callbackContext, GetUInt24(cpu, 20));
        Assert.Equal(0x2006, cpu.GetUint16(28));
        Assert.Equal(originalSp - 3, cpu.SP);

        ReturnFromCallback(cpu);
        Assert.Equal(0x3f0124, cpu.PC);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(callback, cpu.PC);
        Assert.Equal((byte)'B', cpu.Data[16]);
        Assert.Equal(callbackContext, GetUInt24(cpu, 20));

        ReturnFromCallback(cpu);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x23456, cpu.PC);
        Assert.Equal(originalSp + 3, cpu.SP);
        Assert.Equal(2, cpu.GetUint16(16));
        Assert.Equal(arguments, GetUInt24(cpu, argumentCursorAddress));
    }

    [Fact]
    public void StringWalkerExpandsSignedIntegersStringsAndPercentLiterals()
    {
        const int source = 0x1234;
        const int callback = 0x73512;
        const int callbackContext = 0x24567;
        const int stack = 0x2000;
        const int argumentCursorAddress = 0x2100;
        const int arguments = 0x2200;
        const int stringArgument = 0x2300;
        var cpu = CreateCpu(0x3f0122, 0x23456);
        System.Text.Encoding.ASCII.GetBytes("A%d:%s:%%\0")
            .CopyTo(cpu.ProgBytes, source);
        System.Text.Encoding.ASCII.GetBytes("camera\0")
            .CopyTo(cpu.Data, stringArgument);
        cpu.SetUint16(28, stack);
        SetUInt24(cpu, 16, 0x800000 | source);
        SetUInt24(cpu, 20, callback);
        SetUInt24(cpu, stack, callbackContext);
        SetUInt24(cpu, stack + 3, argumentCursorAddress);
        SetUInt24(cpu, argumentCursorAddress, arguments);
        cpu.SetUint16(arguments, unchecked((ushort)-12));
        SetUInt24(cpu, arguments + 2, stringArgument);
        var output = new List<byte>();

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        while (cpu.PC == callback)
        {
            output.Add(cpu.Data[16]);
            Assert.Equal(callbackContext, GetUInt24(cpu, 20));
            ReturnFromCallback(cpu);
            Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        }

        Assert.Equal("A-12:camera:%", System.Text.Encoding.ASCII.GetString([.. output]));
        Assert.Equal(output.Count, cpu.GetUint16(16));
        Assert.Equal(arguments + 5, GetUInt24(cpu, argumentCursorAddress));
        Assert.Equal(0x23456, cpu.PC);
    }

    [Theory]
    [InlineData(0x0905)]
    [InlineData(0x0915)]
    public void PeripheralReceiveServiceCopiesRequestedBytesToTwentyFourBitDestination(int source)
    {
        const int destination = 0x21234;
        var cpu = CreateCpu(0x3f0136, 0x23456);
        cpu.SetUint16(16, source);
        SetUInt24(cpu, 20, destination);
        cpu.Data[24] = 3;
        var values = new Queue<byte>([0x12, 0x34, 0x56]);
        cpu.ReadHooks[source] = _ => values.Dequeue();

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x23456, cpu.PC);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, cpu.Data[destination..(destination + 3)]);
        Assert.Empty(values);
    }

    [Fact]
    public void PeripheralReceiveServiceRejectsUnknownSourceRegister()
    {
        var cpu = CreateCpu(0x3f0136, 0x23456);
        cpu.SetUint16(16, 0x1234);
        SetUInt24(cpu, 20, 0x2000);
        cpu.Data[24] = 1;

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
    }

    [Theory]
    [InlineData(0x0905)]
    [InlineData(0x0915)]
    public void PeripheralTransmitServiceWritesStackCountBytesAndCleansCount(int destination)
    {
        const int source = 0x21234;
        const int stack = 0x22000;
        var cpu = CreateCpu(0x3f0138, 0x23456);
        cpu.SetUint16(16, destination);
        SetUInt24(cpu, 20, source);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = (byte)(stack >> 16);
        cpu.Data[stack] = 3;
        cpu.Data[source] = 0x12;
        cpu.Data[source + 1] = 0x34;
        cpu.Data[source + 2] = 0x56;
        var transmitted = new List<byte>();
        cpu.WriteHooks[destination] = (value, _, _, _) =>
        {
            transmitted.Add(value);
            return false;
        };

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, transmitted);
        Assert.Equal(stack + 1, cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
        Assert.Equal(0x23456, cpu.PC);
    }

    [Fact]
    public void PeripheralTransmitServiceRejectsUnknownDestinationRegister()
    {
        var cpu = CreateCpu(0x3f0138, 0x23456);
        cpu.SetUint16(16, 0x1234);
        cpu.SetUint16(28, 0x2000);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
    }

    [Theory]
    [InlineData(0, 10, 0, 0)]
    [InlineData(42, 10, 4, 2)]
    [InlineData(255, 16, 15, 15)]
    public void UnsignedByteDivisionReturnsQuotientAndRemainder(
        byte dividend, byte divisor, byte quotient, byte remainder)
    {
        var cpu = CreateCpu(0x3f004c, 0x12345);
        cpu.Data[16] = dividend;
        cpu.Data[20] = divisor;

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(quotient, cpu.Data[16]);
        Assert.Equal(remainder, cpu.Data[20]);
        Assert.Equal(0x12345, cpu.PC);
    }

    [Theory]
    [InlineData(0, 2, 0, 0)]
    [InlineData(11, 4, 2, 3)]
    [InlineData(-11, 4, -2, -3)]
    [InlineData(11, -4, -2, 3)]
    public void SignedByteDivisionReturnsQuotientAndRemainder(
        sbyte dividend, sbyte divisor, sbyte quotient, sbyte remainder)
    {
        var cpu = CreateCpu(0x3f004e, 0x12345);
        cpu.Data[16] = unchecked((byte)dividend);
        cpu.Data[20] = unchecked((byte)divisor);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(quotient, unchecked((sbyte)cpu.Data[16]));
        Assert.Equal(remainder, unchecked((sbyte)cpu.Data[20]));
        Assert.Equal(0x12345, cpu.PC);
    }

    [Fact]
    public void InterruptRestoreWithNonzeroNestingDepthFailsClosed()
    {
        var cpu = CreateCpu(0x3f0162, 0x2245);
        cpu.Data[0xf600] = 2;

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));

        Assert.Equal(0x3f0162, cpu.PC);
        Assert.Equal(
            1,
            AsicRom.GetSchedulerRestoreInfo(cpu).RejectedUncapturedInterruptRestores);
    }

    [Fact]
    public void InterruptFinalizeRequiresCurrentEdgeCapture()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x71;
        SetUInt24(cpu, descriptor + 22, 0x0ca573);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        // The initialized scheduler context contains a perfectly valid old
        // frame, but 0x0164 is an interrupt-only ABI and must not accept that
        // persistent snapshot without a matching hardware edge.
        PushRomCall(cpu, 0x3f0164, 0x12345);
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
    }

    [Fact]
    public void InterruptEdgeSnapshotRemainsPrivateUntilFirmwareFinalizesFrame()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0x1fd0;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x71;
        SetUInt24(cpu, descriptor + 22, 0x0ca573);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x00221b, Assert.Single(AsicRom.GetSchedulerContexts(cpu)).SavedPc);

        cpu.SetUint16(0xf608, context);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);

        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));
        Assert.Equal(0x00221b, Assert.Single(AsicRom.GetSchedulerContexts(cpu)).SavedPc);

        // A mismatched visible frame must neither finalize the edge capture
        // nor replace the task's last scheduler-owned context.
        var savedSp = interruptedSp - 9;
        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 9] = 0xff;
        cpu.PC = 0x3f0164;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Equal(0x00221b, Assert.Single(AsicRom.GetSchedulerContexts(cpu)).SavedPc);
    }

    [Fact]
    public void HighPriorityInterruptCaptureSurvivesFirmwareStackSwitch()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0x1fd0;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x71;
        SetUInt24(cpu, descriptor + 22, 0x0ca573);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(0xf608, context);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        cpu.Data[4] = 0xa4;
        cpu.Data[16] = 0x66;
        cpu.Data[0x58] = 1;
        cpu.Data[0x59] = 2;
        cpu.Data[0x5a] = 3;
        cpu.Data[0x5b] = 4;
        cpu.Data[0x5c] = 5;
        cpu.Data[0x5f] = 0x95;
        cpu.Data[interruptedSp + 1] = 0xbb;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = interruptedSp - 9;
        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 1] = 4;
        cpu.Data[savedSp + 2] = 0x15;
        cpu.Data[savedSp + 3] = 0x66;
        cpu.Data[savedSp + 4] = 0;
        cpu.Data[savedSp + 5] = 0x00;
        cpu.Data[savedSp + 6] = 0x20;
        cpu.Data[savedSp + 7] = (byte)(interruptedPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(interruptedPc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(interruptedPc & 0xff);
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, context);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Array.Clear(cpu.Data, 0, 32);
        cpu.Data[0x58] = 0;
        cpu.Data[0x59] = 0;
        cpu.Data[0x5a] = 0;
        cpu.Data[0x5b] = 0;
        cpu.Data[0x5c] = 0;
        cpu.Data[0x5f] = 0;
        cpu.Data[interruptedSp + 1] = 0;
        cpu.SP = 0xf4c8;
        cpu.PC = 0x3f0162;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(interruptedPc, cpu.PC);
        Assert.Equal(interruptedSp, cpu.SP);
        Assert.Equal(0xa4, cpu.Data[4]);
        Assert.Equal(0x66, cpu.Data[16]);
        Assert.Equal(1, cpu.Data[0x58]);
        Assert.Equal(2, cpu.Data[0x59]);
        Assert.Equal(3, cpu.Data[0x5a]);
        Assert.Equal(4, cpu.Data[0x5b]);
        Assert.Equal(5, cpu.Data[0x5c]);
        Assert.Equal(0x95, cpu.SREG);
        Assert.Equal(0xbb, cpu.Data[interruptedSp + 1]);
    }

    [Fact]
    public void HighPriorityInterruptRejectsStackPointerBelowActiveTaskAllocation()
    {
        const int taskContext = 0x2000;
        const int descriptor = taskContext + 31;
        const int interruptedPc = 0x23456;
        const int interruptedSp = 0x1ef0;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, taskContext);
        cpu.SetUint16(taskContext - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x71;
        SetUInt24(cpu, descriptor + 22, 0x0ca573);
        SetSoftwareStackBounds(cpu, new(taskContext, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(0xf608, taskContext);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);

        // Descriptor +33 gives this task a hardware-stack floor of 0x1f00.
        // A lower SP belongs to neither the task nor any firmware-owned shared
        // stack, so mask ROM must fail closed instead of snapshotting it as the
        // active task merely because it is below the task's allocation top.
        Assert.False(AsicRom.CaptureHighPriorityInterrupt(cpu));
    }

    [Fact]
    public void HighPriorityInterruptPreservesSchedulerScanAsDispatcherContext()
    {
        const int taskContext = 0x2000;
        const int descriptor = taskContext + 31;
        const int schedulerPc = 0x22c1;
        const int schedulerSp = 0xf4c8;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, taskContext);
        cpu.SetUint16(taskContext - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x36;
        SetUInt24(cpu, descriptor + 22, 0x04c460);
        SetSoftwareStackBounds(cpu, new(taskContext, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.PC = 0x3f015e;
        SetZ(cpu, taskContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(0x001020, cpu.TranslateDataAddress(0x021020));

        cpu.SetUint16(0xf608, taskContext);
        cpu.PC = schedulerPc;
        cpu.SP = schedulerSp;
        cpu.Data[4] = 0xa4;
        cpu.Data[20] = 0x50;
        cpu.Data[0x5f] = 0x95;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = schedulerSp - 9;
        cpu.SetUint16(0xf7ab + 18, savedSp);
        cpu.Data[savedSp + 1] = 0;
        cpu.Data[savedSp + 2] = 0x15;
        cpu.Data[savedSp + 3] = 0;
        cpu.Data[savedSp + 4] = 0;
        cpu.Data[savedSp + 5] = 0;
        cpu.Data[savedSp + 6] = 0;
        cpu.Data[savedSp + 7] = (byte)(schedulerPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(schedulerPc >> 8);
        cpu.Data[savedSp + 9] = (byte)(schedulerPc & 0xff);
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, 0xf7ab);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        // Scheduler work performed inside the interrupt can install another
        // task's windows before the dispatcher snapshot resumes.
        cpu.ConfigureDataAddressWindow(0x023000, 0x0230ff, 0x003000);
        Array.Clear(cpu.Data, 0, 32);
        cpu.Data[0x5f] = 0;
        cpu.SP = 0xf5fe;
        cpu.PC = 0x3f0162;
        SetZ(cpu, 0xf7ab);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(schedulerPc, cpu.PC);
        Assert.Equal(schedulerSp, cpu.SP);
        Assert.Equal(0xa4, cpu.Data[4]);
        Assert.Equal(0x50, cpu.Data[20]);
        Assert.Equal(0x95, cpu.SREG);
        Assert.Equal(0x001020, cpu.TranslateDataAddress(0x021020));
        Assert.Equal(0x023020, cpu.TranslateDataAddress(0x023020));

        // The dispatcher snapshot is one-shot; later task interrupts must use
        // the firmware-built dispatcher frame instead of replaying stale scan state.
        cpu.PC = 0x3f0162;
        SetZ(cpu, 0xf7ab);
        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
    }

    [Fact]
    public void HighPriorityInterruptCapturesInitialSchedulerScanBeforeFirstTaskContext()
    {
        const int dispatcherContext = 0xf7ab;
        const int schedulerPc = 0x22b8;
        const int schedulerSp = 0xf4c8;
        var cpu = CreateCpu(0, 0);
        cpu.PC = schedulerPc;
        cpu.SP = schedulerSp;
        cpu.Data[16] = 0x22;
        cpu.Data[17] = 0x1b;
        cpu.Data[30] = 0xf2;
        cpu.Data[31] = 0x20;
        cpu.Data[0x5f] = 0x82;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = schedulerSp - 9;
        cpu.SetUint16(dispatcherContext + 18, savedSp);
        cpu.Data[savedSp + 1] = 0;
        cpu.Data[savedSp + 2] = 0x02;
        cpu.Data[savedSp + 3] = 0x22;
        cpu.Data[savedSp + 4] = 0x1b;
        cpu.Data[savedSp + 5] = 0xf2;
        cpu.Data[savedSp + 6] = 0x20;
        cpu.Data[savedSp + 7] = (byte)(schedulerPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(schedulerPc >> 8);
        cpu.Data[savedSp + 9] = (byte)(schedulerPc & 0xff);
        cpu.SP = 0xf5f8;
        cpu.Data[0xf5f9] = 0x6a;
        cpu.Data[0xf5fa] = 0x16;
        cpu.Data[0xf5fb] = 0;
        cpu.PC = 0x3f0164;
        SetZ(cpu, dispatcherContext);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Array.Clear(cpu.Data, 0, 32);
        cpu.PC = 0x3f0162;
        SetZ(cpu, dispatcherContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.Equal(schedulerPc, cpu.PC);
        Assert.Equal(schedulerSp, cpu.SP);
        Assert.Equal(0x22, cpu.Data[16]);
        Assert.Equal(0x1b, cpu.Data[17]);
        Assert.Equal(0xf2, cpu.Data[30]);
        Assert.Equal(0x20, cpu.Data[31]);
        Assert.Equal(0x82, cpu.SREG);
    }

    [Fact]
    public void HighPriorityInterruptCapturesDispatcherOwnedIdleStack()
    {
        const int taskContext = 0x2000;
        const int descriptor = taskContext + 31;
        const int dispatcherContext = 0xf7ab;
        const int interruptedPc = 0x159a;
        const int interruptedSp = 0xf788;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, taskContext);
        cpu.SetUint16(taskContext - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x36;
        SetUInt24(cpu, descriptor + 22, 0x04c460);
        SetSoftwareStackBounds(cpu, new(taskContext, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(0xf608, dispatcherContext);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        cpu.Data[16] = 0xef;
        cpu.Data[17] = 0x15;
        cpu.Data[30] = 0xc0;
        cpu.Data[31] = 0x0a;
        cpu.Data[0x5f] = 0xb4;
        cpu.Data[interruptedSp + 1] = 0xbb;

        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = interruptedSp - 9;
        cpu.SetUint16(dispatcherContext + 18, savedSp);
        cpu.Data[savedSp + 1] = 0;
        cpu.Data[savedSp + 2] = 0x34;
        cpu.Data[savedSp + 3] = 0xef;
        cpu.Data[savedSp + 4] = 0x15;
        cpu.Data[savedSp + 5] = 0xc0;
        cpu.Data[savedSp + 6] = 0x0a;
        cpu.Data[savedSp + 7] = (byte)(interruptedPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(interruptedPc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(interruptedPc & 0xff);
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, dispatcherContext);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Array.Clear(cpu.Data, 0, 32);
        cpu.Data[interruptedSp + 1] = 0;
        cpu.PC = 0x3f0162;
        SetZ(cpu, dispatcherContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(interruptedPc, cpu.PC);
        Assert.Equal(interruptedSp, cpu.SP);
        Assert.Equal(0xef, cpu.Data[16]);
        Assert.Equal(0x15, cpu.Data[17]);
        Assert.Equal(0xc0, cpu.Data[30]);
        Assert.Equal(0x0a, cpu.Data[31]);
        Assert.Equal(0xb4, cpu.SREG);
        Assert.Equal(0xbb, cpu.Data[interruptedSp + 1]);
        Assert.Equal(taskContext, Assert.Single(AsicRom.GetSchedulerContexts(cpu)).Address);
    }

    [Fact]
    public void SchedulerSelectionRetiresAbandonedDispatcherInterruptCapture()
    {
        const int taskContext = 0x2000;
        const int descriptor = taskContext + 31;
        const int dispatcherContext = 0xf7ab;
        const int interruptedPc = 0x159a;
        const int interruptedSp = 0xf788;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, taskContext);
        cpu.SetUint16(taskContext - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x36;
        SetUInt24(cpu, descriptor + 22, 0x04c460);
        SetSoftwareStackBounds(cpu, new(taskContext, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(0xf608, dispatcherContext);
        Array.Clear(cpu.Data, 0, 32);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        cpu.Data[0x5f] = 0xa0;
        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = interruptedSp - 9;
        cpu.SetUint16(dispatcherContext + 18, savedSp);
        cpu.Data[savedSp + 1] = 0;
        cpu.Data[savedSp + 2] = 0x20;
        cpu.Data[savedSp + 3] = 0;
        cpu.Data[savedSp + 4] = 0;
        cpu.Data[savedSp + 5] = 0;
        cpu.Data[savedSp + 6] = 0;
        cpu.Data[savedSp + 7] = (byte)(interruptedPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(interruptedPc >> 8);
        cpu.Data[savedSp + 9] = (byte)(interruptedPc & 0xff);
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, dispatcherContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        // The IRQ requests a reschedule, so firmware does not call 0x0162 for
        // f7ab. Its next mask-ROM boundary is selection of an ordinary task.
        PushRomCall(cpu, 0x3f015c, 0x00229b);
        SetZ(cpu, taskContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        // A later idle-loop interrupt owns a new f7ab continuation and must
        // not collide with the abandoned capture from the prior IRQ.
        cpu.SetUint16(0xf608, dispatcherContext);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));
        Assert.Equal(taskContext, Assert.Single(AsicRom.GetSchedulerContexts(cpu)).Address);
    }

    [Fact]
    public void NewDispatcherInterruptRetiresAbandonedCaptureBeforeTaskSelection()
    {
        const int dispatcherContext = 0xf7ab;
        const int interruptedPc = 0x22c1;
        const int interruptedSp = 0xf4c8;
        var cpu = CreateCpu(0, 0);
        Array.Clear(cpu.Data, 0, 32);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        cpu.Data[0x5f] = 0x94;
        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);
        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));

        var savedSp = interruptedSp - 9;
        cpu.SetUint16(dispatcherContext + 18, savedSp);
        cpu.Data[savedSp + 1] = 0;
        cpu.Data[savedSp + 2] = 0x14;
        cpu.Data[savedSp + 3] = 0;
        cpu.Data[savedSp + 4] = 0;
        cpu.Data[savedSp + 5] = 0;
        cpu.Data[savedSp + 6] = 0;
        cpu.Data[savedSp + 7] = (byte)(interruptedPc >> 16);
        cpu.Data[savedSp + 8] = (byte)(interruptedPc >> 8);
        cpu.Data[savedSp + 9] = (byte)(interruptedPc & 0xff);
        cpu.SP = 0xf5f8;
        cpu.PC = 0x3f0164;
        SetZ(cpu, dispatcherContext);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        // The handler requests a reschedule and RETI enters the interruptible
        // scheduler scan. A second edge can arrive before task selection calls
        // 0x015c, and it supersedes the abandoned f7ab continuation.
        Array.Clear(cpu.Data, 0, 32);
        cpu.PC = interruptedPc;
        cpu.SP = interruptedSp;
        AvrInterrupt.Execute(cpu, AsicInterruptController.HighPriorityVectorWord);

        Assert.True(AsicRom.CaptureHighPriorityInterrupt(cpu));
    }

    [Fact]
    public void InterruptedRestoreRejectsPlausibleFrameWithoutCurrentEdgeCapture()
    {
        const int context = 0x2000;
        const int descriptor = context + 31;
        const int savedSp = 0x1f20;
        const int resumePc = 0x34567;
        var cpu = CreateCpu(0x3f015c, 0x12345);
        SetZ(cpu, context);
        cpu.SetUint16(context - 4, descriptor);
        cpu.Data[descriptor + 5] = 0x71;
        SetUInt24(cpu, descriptor + 22, 0x0ca573);
        SetSoftwareStackBounds(cpu, new(context, descriptor, 0x1ffe, 0x1000));
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 1] = 3;
        cpu.Data[savedSp + 2] = 0x9f;
        cpu.Data[savedSp + 3] = 0x66;
        cpu.Data[savedSp + 4] = 0x77;
        cpu.Data[savedSp + 5] = 0x9a;
        cpu.Data[savedSp + 6] = 0xbc;
        cpu.Data[savedSp + 7] = (byte)(resumePc >> 16);
        cpu.Data[savedSp + 8] = (byte)(resumePc >> 8 & 0xff);
        cpu.Data[savedSp + 9] = (byte)(resumePc & 0xff);
        cpu.PC = 0x3f0162;
        SetZ(cpu, context);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Equal(0x3f0162, cpu.PC);
        Assert.Equal(
            1,
            AsicRom.GetSchedulerRestoreInfo(cpu).RejectedUncapturedInterruptRestores);
    }

    [Fact]
    public void UncapturedDispatcherFrameFailsClosedWithoutHardwareEdge()
    {
        const int context = 0xf7ab;
        const int savedSp = 0xf4bf;
        const int resumePc = 0x22b8;
        var cpu = CreateCpu(0x3f0162, 0x12345);
        SetZ(cpu, context);
        cpu.Data[context] = 0xfd;
        cpu.Data[context + 1] = 0xfd;
        cpu.SetUint16(context + 18, savedSp);
        cpu.Data[savedSp + 1] = 3;
        cpu.Data[savedSp + 2] = 0x9f;
        cpu.Data[savedSp + 7] = (byte)(resumePc >> 16);
        cpu.Data[savedSp + 8] = (byte)(resumePc >> 8);
        cpu.Data[savedSp + 9] = (byte)(resumePc & 0xff);
        cpu.Data[0x58] = 1;
        cpu.Data[0x59] = 2;
        cpu.Data[0x5a] = 2;
        cpu.Data[0x5c] = 5;

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.Equal(0x3f0162, cpu.PC);
        Assert.Equal(1, cpu.Data[0x58]);
        Assert.Equal(2, cpu.Data[0x59]);
        Assert.Equal(2, cpu.Data[0x5a]);
        Assert.Equal(0, cpu.Data[0x5b]);
        Assert.Equal(5, cpu.Data[0x5c]);
        Assert.Equal(
            1,
            AsicRom.GetSchedulerRestoreInfo(cpu).RejectedUncapturedInterruptRestores);
    }

    [Fact]
    public void FirmwareUploadReadsRomPrivateStagingRatherThanRuntimeData()
    {
        const int source = 0x27000;
        const int destination = 0x200;
        var cpu = CreatePreparedFirmwareUploadCpu(0x3f0076, beforePrepare =>
        {
            beforePrepare.ProgBytes[0x200] = 0x12;
            beforePrepare.ProgBytes[0x201] = 0x34;
            beforePrepare.ProgBytes[0x202] = 0x56;
        });
        cpu.Data[source] = 0xa1;
        cpu.Data[source + 1] = 0xb2;
        cpu.Data[source + 2] = 0xc3;
        SetUploadPointer(cpu, destination);
        SetUInt24(cpu, 16, source);
        SetUInt24(cpu, 20, 3);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 },
            AsicRom.GetUploadedFirmware(cpu).Span[destination..(destination + 3)].ToArray());
        Assert.Equal(3, AsicRom.GetUploadedFirmwareByteCount(cpu));
        Assert.All(cpu.Data[destination..(destination + 3)], value => Assert.Equal(0, value));
    }

    [Fact]
    public void FirmwareUploadPreparationSeparatesRuntimeDataFromCompleteStagingImage()
    {
        const int mainSource = 0x200;
        const int destination = 0x27000;
        const int mainInitializedLength = 0x1f2a;
        const int uploadInitializedLength = 0x4d12;
        const int allocationLength = 0xfa9b;
        const int mutexOffset = 0x6bf8;
        var cpu = CreateCpu(0x3f0078, 0x12345, 0x40000);
        cpu.ProgBytes[mainSource] = 0x12;
        cpu.ProgBytes[mainSource + 0x1459] = 0x7e;
        cpu.ProgBytes[mainSource + 0x145a] = 0xac;
        cpu.ProgBytes[mainSource + 0x145b] = 0x1b;
        cpu.ProgBytes[mainSource + mainInitializedLength - 1] = 0x34;
        cpu.ProgBytes[mainSource + mainInitializedLength] = 0x56;
        cpu.ProgBytes[mainSource + uploadInitializedLength - 1] = 0x9a;
        cpu.Data[destination - 1] = 0x56;
        cpu.Data[destination + mainInitializedLength] = 0xaa;
        cpu.Data[destination + mutexOffset] = 0xbb;
        cpu.Data[destination + allocationLength - 1] = 0xcc;
        cpu.Data[destination + allocationLength] = 0x78;
        cpu.SetUint16(30, 0);
        cpu.SetUint16(16, 0);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0074, 0x12345);
        SetUploadPointer(cpu, 0);
        cpu.SetUint16(16, 0);
        cpu.SetUint16(20, 0);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12, cpu.Data[destination]);
        Assert.Equal(new byte[] { 0x7e, 0xac, 0x1b },
            cpu.Data[(destination + 0x1459)..(destination + 0x145c)]);
        Assert.Equal(0x34, cpu.Data[destination + mainInitializedLength - 1]);
        Assert.Equal(0, cpu.Data[destination + mainInitializedLength]);
        Assert.Equal(0, cpu.Data[destination + uploadInitializedLength - 1]);
        Assert.Equal(0, cpu.Data[destination + mutexOffset]);
        Assert.Equal(0, cpu.Data[destination + allocationLength - 1]);
        Assert.Equal(0x56, cpu.Data[destination - 1]);
        Assert.Equal(0x78, cpu.Data[destination + allocationLength]);
    }

    [Fact]
    public void ObservedUploadSequenceReconstructsPreparedLowSbnImage()
    {
        const int returnAddress = 0x12345;
        var cpu = CreateCpu(0x3f0078, returnAddress, 0x40000);
        cpu.ProgBytes[0x0200] = 0x12;
        cpu.ProgBytes[0x4f11] = 0x34;
        cpu.SetUint16(30, 0);
        cpu.SetUint16(16, 0);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0074, returnAddress);
        SetUploadPointer(cpu, 0);
        cpu.SetUint16(16, 0);
        cpu.SetUint16(20, 0);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0072, returnAddress);
        SetUploadPointer(cpu, 0x28471);
        SetUInt24(cpu, 16, 0x247f);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0076, returnAddress);
        SetUploadPointer(cpu, 0x14ff);
        SetUInt24(cpu, 16, 0x282ff);
        SetUInt24(cpu, 20, 0x0172);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0072, returnAddress);
        SetUploadPointer(cpu, 0x2a8f0);
        SetUInt24(cpu, 16, 0xc1ab);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0076, returnAddress);
        SetUploadPointer(cpu, 0x0200);
        SetUInt24(cpu, 16, 0x27000);
        SetUInt24(cpu, 20, 0x12ff);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        var image = AsicRom.GetUploadedFirmware(cpu).Span;
        Assert.Equal(0xfc9b, image.Length);
        Assert.Equal(0xfa9b, AsicRom.GetUploadedFirmwareByteCount(cpu));
        Assert.All(image[..0x0200].ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0x12, image[0x0200]);
        Assert.Equal(0x34, image[0x4f11]);
        Assert.All(image[0x4f12..].ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void FirmwareUploadRejectsDataSourceOutsideImage()
    {
        var cpu = CreatePreparedFirmwareUploadCpu(0x3f0076);
        SetUploadPointer(cpu, 0x200);
        SetUInt24(cpu, 16, cpu.Data.Length - 1);
        SetUInt24(cpu, 20, 2);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.True(AsicRom.GetUploadedFirmware(cpu).IsEmpty);
    }

    [Fact]
    public void FirmwareUploadRelocatesImplicitSpanQueuedBeforeExplicitMapping()
    {
        const int relocationOffset = 0x26e00;
        const int implicitSource = 0x28471;
        const int implicitDestination = implicitSource - relocationOffset;
        const int explicitSource = 0x282ff;
        const int explicitDestination = explicitSource - relocationOffset;
        var cpu = CreatePreparedFirmwareUploadCpu(0x3f0072, beforePrepare =>
        {
            beforePrepare.ProgBytes[implicitDestination] = 0x12;
            beforePrepare.ProgBytes[implicitDestination + 1] = 0x34;
            beforePrepare.ProgBytes[explicitDestination] = 0x56;
        });
        SetUploadPointer(cpu, implicitSource);
        SetUInt24(cpu, 16, 2);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));
        Assert.True(AsicRom.GetUploadedFirmware(cpu).IsEmpty);

        PushRomCall(cpu, 0x3f0076, 0x23456);
        SetUploadPointer(cpu, explicitDestination);
        SetUInt24(cpu, 16, explicitSource);
        SetUInt24(cpu, 20, 1);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        var firmware = AsicRom.GetUploadedFirmware(cpu).Span;
        Assert.Equal(0x56, firmware[explicitDestination]);
        Assert.Equal(new byte[] { 0x12, 0x34 },
            firmware[implicitDestination..(implicitDestination + 2)].ToArray());
        Assert.Equal(3, AsicRom.GetUploadedFirmwareByteCount(cpu));
    }

    [Fact]
    public void ZeroLengthUploadRecordsDestinationAfterImageExists()
    {
        var cpu = CreatePreparedFirmwareUploadCpu(0x3f0076);
        SetUploadPointer(cpu, 0x200);
        SetUInt24(cpu, 16, 0x27000);
        SetUInt24(cpu, 20, 1);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0076, 0x23456);
        SetUploadPointer(cpu, 0x200);
        SetUInt24(cpu, 16, 0);
        SetUInt24(cpu, 20, 0);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x200, AsicRom.GetUploadedFirmwareZeroLengthDestination(cpu));
    }

    [Fact]
    public void FirmwareTransferFailsClosedBeforeUploadLifecycleAbiRuns()
    {
        var cpu = CreateCpu(0x3f0076, 0x12345);
        SetUploadPointer(cpu, 0x200);
        SetUInt24(cpu, 16, 0x27000);
        SetUInt24(cpu, 20, 1);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
        Assert.True(AsicRom.GetUploadedFirmware(cpu).IsEmpty);
    }

    [Fact]
    public void FirmwareUploadSessionRejectsUnobservedNonzeroArguments()
    {
        var cpu = CreateCpu(0x3f0078, 0x12345);
        cpu.SetUint16(30, 1);

        Assert.Equal(AsicRomDispatchResult.Unknown, AsicRom.Dispatch(cpu));
    }

    [Fact]
    public void LogicalProgramCopyMapsHighBitAndWritesExtendedData()
    {
        const int physicalSource = 0x54597e;
        const int logicalSource = physicalSource | 0x800000;
        const int destination = 0x2d9c4;
        var cpu = CreateCpu(0x3f006e, 0x12345);
        cpu.ProgBytes[physicalSource] = 0x12;
        cpu.ProgBytes[physicalSource + 1] = 0x34;
        SetUploadPointer(cpu, logicalSource);
        SetUInt24(cpu, 16, destination);
        cpu.SetUint16(20, 2);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(new byte[] { 0x12, 0x34 }, cpu.Data[destination..(destination + 2)]);
    }

    [Fact]
    public void LogicalProgramCopyUsesMappedAsicObjectBytes()
    {
        const int logicalSource = 0xc1ff53;
        const int physicalSource = logicalSource & 0x7fffff;
        const int destination = 0x2d9c4;
        var cpu = CreateCpu(0x3f006e, 0x12345, 0x1000000);
        new byte[]
        {
            0xe7, 0x77, 0x0e,
            0x3c, 0x7a, 0x0e,
            0x6a, 0x7b, 0x0e,
            0xc8, 0x7c, 0x0e,
            0x3b, 0x7c, 0x0e,
            0x45, 0xff, 0xc1,
            0x00, 0x00, 0x00, 0x00,
        }.CopyTo(cpu.ProgBytes, physicalSource);
        _ = new AsicFlashMemory(cpu);
        SetUploadPointer(cpu, logicalSource);
        SetUInt24(cpu, 16, destination);
        cpu.SetUint16(20, 22);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(new byte[] { 0xe7, 0x77, 0x0e },
            cpu.Data[destination..(destination + 3)]);
        Assert.Equal(new byte[] { 0x45, 0xff, 0xc1 },
            cpu.Data[(destination + 15)..(destination + 18)]);
        Assert.All(cpu.Data[(destination + 18)..(destination + 22)], value => Assert.Equal(0, value));
    }

    [Fact]
    public void MemoryCopyReadsUnmarkedSourceFromDataMemory()
    {
        const int source = 0x156c;
        const int destination = 0x1592;
        var cpu = CreateCpu(0x3f006e, 0x12345);
        new byte[] { 0x54, 0x1d, 0x08, 0x36 }.CopyTo(cpu.Data, source);
        SetUploadPointer(cpu, source);
        SetUInt24(cpu, 16, destination);
        cpu.SetUint16(20, 4);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(new byte[] { 0x54, 0x1d, 0x08, 0x36 },
            cpu.Data[destination..(destination + 4)]);
    }

    [Fact]
    public void MemoryCopyTreatsD6BankAsDataDespiteFlashAliasBit()
    {
        const int source = 0xd6e5cb;
        const int physicalRomAlias = source & 0x7fffff;
        const int destination = 0x5058d;
        var cpu = CreateCpu(0x3f006e, 0x12345, 0x1000000);
        new byte[] { 0xe7, 0x77, 0x0e, 0x3c }.CopyTo(cpu.Data, source);
        Enumerable.Repeat((byte)0xff, 4).ToArray()
            .CopyTo(cpu.ProgBytes, physicalRomAlias);
        SetUploadPointer(cpu, source);
        SetUInt24(cpu, 16, destination);
        cpu.SetUint16(20, 4);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(new byte[] { 0xe7, 0x77, 0x0e, 0x3c },
            cpu.Data[destination..(destination + 4)]);
    }

    [Fact]
    public void BlockCopyServiceCopiesSixtyFourByteUnitsAndConsumesCount()
    {
        const int source = 0x038167;
        const int destination = 0x108c82;
        const int stack = 0x2000;
        var cpu = CreateCpu(0x3f015a, 0x12345, 0x120000);
        for (var index = 0; index < 128; index++)
        {
            cpu.Data[source + index] = (byte)(index ^ 0x5a);
        }
        SetUInt24(cpu, 16, destination);
        SetUInt24(cpu, 20, source);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = 0;
        cpu.SetUint16(stack, 2);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(stack + 2, cpu.GetUint16(28));
        Assert.Equal(cpu.Data[source..(source + 128)], cpu.Data[destination..(destination + 128)]);
    }

    [Fact]
    public void AggregatePushCopiesDataToDescendingExtendedSoftwareStack()
    {
        const int source = 0x031f73;
        const int stack = 0x03e038;
        const int destination = stack - 6;
        var cpu = CreateCpu(0x3f0070, 0x12345, 0x1000000);
        new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(cpu.Data, source);
        cpu.SetUint16(28, stack);
        cpu.Data[0x5a] = (byte)(stack >> 16);
        SetUploadPointer(cpu, source);
        cpu.SetUint16(16, 6);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(destination,
            cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 },
            cpu.Data[destination..(destination + 6)]);
    }

    [Fact]
    public void DataFillConsumesSixteenBitCountFromSoftwareStack()
    {
        const int destination = 0x2921f;
        var cpu = CreateCpu(0x3f00a4, 0x12345);
        SetUInt24(cpu, 16, destination);
        cpu.Data[20] = 0x41;
        cpu.SetUint16(28, 0x2000);
        cpu.SetUint16(0x2000, 0x17);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(0x2002, cpu.GetUint16(28));
        Assert.All(cpu.Data[destination..(destination + 0x17)], value => Assert.Equal(0x41, value));
    }

    [Theory]
    [InlineData(0, 3, new byte[] { 0, 1, 2, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 13, 14, 15 })]
    [InlineData(3, 0, new byte[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 10, 11, 12, 13, 14, 15 })]
    public void DataMoveHandlesOverlappingRangesAndConsumesBankedCount(
        int sourceOffset,
        int destinationOffset,
        byte[] expected)
    {
        const int buffer = 0xd60100;
        const int stackPointer = 0xd6f000;
        var cpu = CreateCpu(0x3f00a2, 0x12345, 0x1000000);
        Enumerable.Range(0, 16).Select(value => (byte)value).ToArray().CopyTo(cpu.Data, buffer);
        SetUInt24(cpu, 16, buffer + destinationOffset);
        SetUInt24(cpu, 20, buffer + sourceOffset);
        cpu.SetUint16(28, stackPointer);
        cpu.Data[0x5a] = (byte)(stackPointer >> 16);
        cpu.SetUint16(stackPointer, 10);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal(buffer + destinationOffset, GetUInt24(cpu, 16));
        Assert.Equal(stackPointer + 2,
            cpu.GetUint16(28) | cpu.Data[0x5a] << 16);
        Assert.Equal(expected, cpu.Data[buffer..(buffer + 16)]);
    }

    [Fact]
    public void DataFillReadsCountFromBankedSoftwareStack()
    {
        const int destination = 0xd6c7df;
        const int stackPointer = 0xd6d802;
        var cpu = CreateCpu(0x3f00a4, 0x12345, 0x1000000);
        SetUInt24(cpu, 16, destination);
        cpu.Data[20] = 0x41;
        cpu.SetUint16(28, stackPointer);
        cpu.Data[0x5a] = (byte)(stackPointer >> 16);
        cpu.SetUint16(stackPointer, 4);
        cpu.SetUint16(stackPointer & 0xffff, 0x00d7);

        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        Assert.Equal(0x12345, cpu.PC);
        Assert.Equal((stackPointer + 2) & 0xffff, cpu.GetUint16(28));
        Assert.Equal((byte)((stackPointer + 2) >> 16), cpu.Data[0x5a]);
        Assert.Equal(new byte[] { 0x41, 0x41, 0x41, 0x41 }, cpu.Data[destination..(destination + 4)]);
        Assert.Equal(0, cpu.Data[destination + 4]);
    }

    static Cpu CreateCpu(int romEntry, int returnAddress, int dataSize = 0x30000)
    {
        var cpu = new Cpu(new byte[0x800000], dataSize)
        {
            PC = romEntry,
            SP = 0xffd,
        };
        cpu.Data[0x1000] = (byte)returnAddress;
        cpu.Data[0x0fff] = (byte)(returnAddress >> 8);
        cpu.Data[0x0ffe] = (byte)(returnAddress >> 16);
        return cpu;
    }

    static Cpu CreatePreparedFirmwareUploadCpu(int entry, Action<Cpu>? beforePrepare = null)
    {
        const int returnAddress = 0x12345;
        var cpu = CreateCpu(0x3f0078, returnAddress, 0x40000);
        beforePrepare?.Invoke(cpu);
        cpu.SetUint16(30, 0);
        cpu.SetUint16(16, 0);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, 0x3f0074, returnAddress);
        SetUploadPointer(cpu, 0);
        cpu.SetUint16(16, 0);
        cpu.SetUint16(20, 0);
        Assert.Equal(AsicRomDispatchResult.Handled, AsicRom.Dispatch(cpu));

        PushRomCall(cpu, entry, returnAddress);
        return cpu;
    }

    static void PushRomCall(Cpu cpu, int entry, int returnAddress)
    {
        var stackTop = cpu.SP;
        cpu.Data[stackTop] = (byte)returnAddress;
        cpu.Data[stackTop - 1] = (byte)(returnAddress >> 8);
        cpu.Data[stackTop - 2] = (byte)(returnAddress >> 16);
        cpu.SP = stackTop - 3;
        cpu.PC = entry;
    }

    static uint GetUInt32(Cpu cpu, int register) =>
        (uint)(cpu.Data[register] |
               (cpu.Data[register + 1] << 8) |
               (cpu.Data[register + 2] << 16) |
               (cpu.Data[register + 3] << 24));

    static int GetUInt24(Cpu cpu, int register) =>
        cpu.Data[register] | cpu.Data[register + 1] << 8 | cpu.Data[register + 2] << 16;

    static void ReturnFromCallback(Cpu cpu)
    {
        cpu.SetProgWord(cpu.PC, 0x9508);
        AvrInstruction.Execute(cpu);
    }

    static void SetUInt32(Cpu cpu, int register, uint value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
        cpu.Data[register + 2] = (byte)(value >> 16);
        cpu.Data[register + 3] = (byte)(value >> 24);
    }

    static void SetSoftwareStackBounds(Cpu cpu, SoftwareStackBounds bounds)
    {
        // Native construction records the 16-bit hardware-SP allocation at
        // descriptor +31/+33 and the independent compiler software-stack
        // range at +37/+51. The physical memory bank is shared by both.
        int context = bounds.Context;
        int descriptor = bounds.Descriptor;
        cpu.SetUint16(descriptor + 31, context - 2);
        SetUInt24(
            cpu,
            descriptor + 33,
            (bounds.Floor & 0xff0000) | ((context - 0x100) & 0xffff));
        cpu.SetUint16(descriptor + 37, bounds.Top);
        cpu.SetUint16(descriptor + 51, bounds.Floor);
        cpu.SetUint16(context + 26, bounds.Top);
        cpu.Data[context + 28] = 0;
        if (cpu.GetUint16(descriptor) == 0 && cpu.GetUint16(descriptor + 2) == 0)
        {
            cpu.SetUint16(descriptor, 0xfdfd);
            cpu.SetUint16(descriptor + 2, descriptor);
        }

        // Native descriptor construction prebuilds the first scheduler frame.
        // Restoring it resumes at the firmware's 0x00221b task trampoline; the
        // two bytes above the restored SP are the descriptor pointer consumed
        // by that trampoline's opening POP pair.
        const int startupTrampoline = 0x00221b;
        var savedSp = context - 14;
        cpu.SetUint16(descriptor + 18, savedSp);
        cpu.Data[savedSp + 7] = (byte)(startupTrampoline >> 16);
        cpu.Data[savedSp + 8] = (byte)(startupTrampoline >> 8);
        cpu.Data[savedSp + 9] = (byte)(startupTrampoline & 0xff);
    }

    static void SetUInt24(Cpu cpu, int address, int value)
    {
        cpu.Data[address] = (byte)value;
        cpu.Data[address + 1] = (byte)(value >> 8);
        cpu.Data[address + 2] = (byte)(value >> 16);
    }

    static void SetZ(Cpu cpu, int address)
    {
        cpu.SetUint16(30, address);
        cpu.Data[0x5b] = (byte)(address >> 16);
    }

    static void SetUploadPointer(Cpu cpu, int address)
    {
        cpu.SetUint16(30, address);
        cpu.Data[19] = (byte)(address >> 16);
    }
}
