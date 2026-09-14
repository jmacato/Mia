// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicExecutableRamTests
{
    [Fact]
    public void FetchesCurrentAndOperandWordsFromProvenRamAlias()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000)
        {
            PC = AsicExecutableRam.ProgramWordStart,
        };
        cpu.SetProgWord(AsicExecutableRam.ProgramWordStart, 0xbbaa);
        cpu.SetProgWord(AsicExecutableRam.ProgramWordStart + 1, 0xddcc);
        cpu.SetUint16(AsicExecutableRam.DataStart, 0x940e);
        cpu.SetUint16(AsicExecutableRam.DataStart + 2, 0x1234);

        AsicExecutableRam.Attach(cpu);

        Assert.Equal(0x940e, cpu.GetProgWord(AsicExecutableRam.ProgramWordStart));
        Assert.Equal(0x1234, cpu.GetProgWord(AsicExecutableRam.ProgramWordStart + 1));
        Assert.Equal(0xbbaa,
            cpu.ProgBytes[AsicExecutableRam.ProgramWordStart * 2] |
            cpu.ProgBytes[AsicExecutableRam.ProgramWordStart * 2 + 1] << 8);
        Assert.Equal(0xddcc,
            cpu.ProgBytes[(AsicExecutableRam.ProgramWordStart + 1) * 2] |
            cpu.ProgBytes[(AsicExecutableRam.ProgramWordStart + 1) * 2 + 1] << 8);
    }

    [Fact]
    public void FallsThroughToProgramFlashOutsideProvenAlias()
    {
        var cpu = new Cpu(new byte[0x800000], 0x1000000)
        {
            PC = AsicExecutableRam.ProgramWordStart - 1,
        };
        cpu.SetProgWord(cpu.PC, 0x4321);

        AsicExecutableRam.Attach(cpu);

        Assert.Equal(0x4321, cpu.GetProgWord(cpu.PC));
    }
}
