// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicFlashMemoryTests
{
    [Fact]
    public void ReadArrayModeAliasesProgramFlashIntoUpperDataSpace()
    {
        var program = new byte[0x580100];
        program[0x010a] = 0x94;
        program[0x544f9d] = 0x02;
        program[0x544faf] = 0x01;
        program[0x55ffff] = 0x5a;
        program[0x580042] = 0xa5;
        var cpu = new Cpu(program, 0xd80100);
        _ = new AsicFlashMemory(cpu);

        Assert.Equal(0x94, cpu.ReadData(0x80010a));
        Assert.Equal(0x02, cpu.ReadData(0xd44f9d));
        Assert.Equal(0x01, cpu.ReadData(0xd44faf));
        Assert.Equal(0x5a, cpu.ReadData(0xd5ffff));
        Assert.Equal(0xa5, cpu.ReadData(0xd80042));
    }

    [Fact]
    public void ExternalDataRamApertureSeparatesApplicationAndGdfsFlashAliases()
    {
        var program = new byte[0x580100];
        program[0x560000] = 0x16;
        program[0x57ffff] = 0x17;
        program[0x580000] = 0x18;
        var cpu = new Cpu(program, 0xd80100);
        cpu.Data[0xd60000] = 0xd6;
        cpu.Data[0xd7ffff] = 0xd7;

        _ = new AsicFlashMemory(cpu);

        Assert.Equal(0xd6, cpu.ReadData(0xd60000));
        Assert.Equal(0xd7, cpu.ReadData(0xd7ffff));
        Assert.Equal(0x18, cpu.ReadData(0xd80000));

        cpu.WriteData(0xd60000, 0x66);
        cpu.WriteData(0xd7ffff, 0x77);
        Assert.Equal(0x66, cpu.ReadData(0xd60000));
        Assert.Equal(0x77, cpu.ReadData(0xd7ffff));
        Assert.Equal(0x16, program[0x560000]);
        Assert.Equal(0x17, program[0x57ffff]);
    }

    [Fact]
    public void IntelIdentifierCommandReportsManufacturerUntilReset()
    {
        var program = new byte[0x580100];
        program[0] = 0x12;
        var cpu = new Cpu(program, 0xd80100);
        _ = new AsicFlashMemory(cpu);

        cpu.WriteData(0x800000, 0x90);
        cpu.WriteData(0x800001, 0x00);
        Assert.Equal(0x89, cpu.ReadData(0x800000));
        Assert.Equal(0x00, cpu.ReadData(0x800001));
        Assert.Equal(0x6c, cpu.ReadData(0x800002));
        Assert.Equal(0x88, cpu.ReadData(0x800003));

        cpu.WriteData(0x800000, 0xff);
        cpu.WriteData(0x800001, 0x00);
        Assert.Equal(0x12, cpu.ReadData(0x800000));
    }

    [Fact]
    public void IntelIdentifierCommandReadsConfiguredUserProtectionRegister()
    {
        var program = new byte[0x580100];
        program[0x010a] = 0x12;
        var cpu = new Cpu(program, 0xd80100);
        var userOtp = Convert.FromHexString("321A065432100654");
        _ = new AsicFlashMemory(cpu, userOtp);

        WriteWord(cpu, 0x800000, 0x0090);

        for (var index = 0; index < userOtp.Length; index++)
        {
            Assert.Equal(userOtp[index], cpu.ReadData(0x80010a + index));
        }

        WriteWord(cpu, 0x800000, 0x00ff);
        Assert.Equal(0x12, cpu.ReadData(0x80010a));
    }

    [Fact]
    public void UnconfiguredUserProtectionRegisterIsErasedInIdentifierMode()
    {
        var program = new byte[0x580100];
        program[0x010a] = 0x12;
        var cpu = new Cpu(program, 0xd80100);
        _ = new AsicFlashMemory(cpu);

        WriteWord(cpu, 0x800000, 0x0090);

        Assert.Equal(0xff, cpu.ReadData(0x80010a));
    }

    [Fact]
    public void OrdinaryWritesDoNotMutateReadOnlyFlash()
    {
        var program = new byte[0x580100];
        program[0x580042] = 0xa5;
        var cpu = new Cpu(program, 0xd80100);
        _ = new AsicFlashMemory(cpu);

        cpu.WriteData(0xd80042, 0x5a);

        Assert.Equal(0xa5, cpu.ReadData(0xd80042));
        Assert.Equal(0, cpu.Data[0xd80042]);
    }

    [Fact]
    public void IntelProgramSequenceWritesWordAndReportsReadyStatus()
    {
        var program = new byte[0x580100];
        var cpu = new Cpu(program, 0xd80100);
        _ = new AsicFlashMemory(cpu);

        WriteWord(cpu, 0xd80042, 0x0050);
        WriteWord(cpu, 0xd80042, 0x0040);
        WriteWord(cpu, 0xd80042, 0x1234);

        Assert.Equal(0x80, cpu.ReadData(0xd80042));
        Assert.Equal(0, cpu.ReadData(0xd80043));
        WriteWord(cpu, 0xd80042, 0x00ff);
        Assert.Equal(0x34, cpu.ReadData(0xd80042));
        Assert.Equal(0x12, cpu.ReadData(0xd80043));
    }

    [Fact]
    public void IntelEraseSequenceErasesContainingMainBlock()
    {
        var program = new byte[0x590100];
        program[0x580042] = 0x12;
        program[0x590042] = 0x34;
        var cpu = new Cpu(program, 0xd90100);
        _ = new AsicFlashMemory(cpu);

        WriteWord(cpu, 0xd80042, 0x0020);
        WriteWord(cpu, 0xd80042, 0x00d0);

        Assert.Equal(0x80, cpu.ReadData(0xd80042));
        WriteWord(cpu, 0xd80042, 0x00ff);
        Assert.Equal(0xff, cpu.ReadData(0xd80042));
        Assert.Equal(0x34, cpu.ReadData(0xd90042));
    }

    [Fact]
    public void ThreadedNorReceivesOneOwnedTransactionPerCommandWord()
    {
        var program = new byte[0x580100];
        program[0] = 0x12;
        var cpu = new Cpu(program, 0xd80100);
        using var worker = new MiaWorker("test NOR owner");
        var flash = new AsicFlashMemory(cpu, worker: worker);

        WriteWord(cpu, 0x800000, 0x0090);

        Assert.Equal(2, flash.WriteCount);
        Assert.Equal(0, worker.CompletedWorkCount);
        Assert.Equal(0x89, cpu.ReadData(0x800000));
        flash.Synchronize();
        var identifierWork = worker.CompletedWorkCount;
        Assert.True(identifierWork >= 1);

        WriteWord(cpu, 0x800000, 0x00ff);

        Assert.Equal(4, flash.WriteCount);
        Assert.Equal(0x12, cpu.ReadData(0x800000));
        flash.Synchronize();
        Assert.True(worker.CompletedWorkCount > identifierWork);
    }

    [Fact]
    public void ThreadedNorBatchesACompleteProgramCommandSequence()
    {
        var program = new byte[0x580100];
        var cpu = new Cpu(program, 0xd80100);
        using var worker = new MiaWorker("test NOR owner");
        var flash = new AsicFlashMemory(cpu, worker: worker);

        WriteWord(cpu, 0xd80042, 0x0050);
        WriteWord(cpu, 0xd80042, 0x0040);

        Assert.Equal(0, worker.CompletedWorkCount);

        WriteWord(cpu, 0xd80042, 0x1234);

        flash.Synchronize();
        Assert.True(worker.CompletedWorkCount >= 1);
        Assert.Equal(1, flash.ClearStatusCommandCount);
        Assert.Equal(1, flash.ProgramSetupCommandCount);
        Assert.Equal(1, flash.ProgramWordCount);
        Assert.Equal(0x80, cpu.ReadData(0xd80042));
        Assert.True(worker.CompletedWorkCount >= 1);
    }

    static void WriteWord(Cpu cpu, int address, ushort value)
    {
        cpu.WriteData(address, (byte)value);
        cpu.WriteData(address + 1, (byte)(value >> 8));
    }
}
