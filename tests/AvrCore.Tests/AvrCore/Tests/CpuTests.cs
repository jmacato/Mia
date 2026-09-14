// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js cpu.spec.ts)

using Xunit;

namespace AvrCore.Tests;

public class CpuTests
{
    [Fact]
    public void InitialStackPointerIsLastByteOfInternalSram()
    {
        var cpu = new Cpu(new byte[0x800], 0x1000);
        Assert.Equal(0x10ff, cpu.SP);
    }

    [Fact]
    public void DataAddressWindowTranslatesOnlyConfiguredLogicalRange()
    {
        var cpu = new Cpu(new byte[0x800], 0xd70000);

        cpu.ConfigureDataAddressWindow(0x02b100, 0x02b1ff, 0xd6b100);

        Assert.Equal(0xd6b100, cpu.TranslateDataAddress(0x02b100));
        Assert.Equal(0xd6b1ff, cpu.TranslateDataAddress(0x02b1ff));
        Assert.Equal(0x02b0ff, cpu.TranslateDataAddress(0x02b0ff));
        Assert.Equal(0x02b200, cpu.TranslateDataAddress(0x02b200));
        Assert.Equal(0x02b180, cpu.UntranslateDataAddress(0xd6b180));
        Assert.Equal(0xd6b200, cpu.UntranslateDataAddress(0xd6b200));
    }

    [Fact]
    public void ResetClearsDataAddressWindow()
    {
        var cpu = new Cpu(new byte[0x800], 0xd70000);
        cpu.ConfigureDataAddressWindow(0x02b100, 0x02b1ff, 0xd6b100);

        cpu.Reset();

        Assert.Equal(0x02b100, cpu.TranslateDataAddress(0x02b100));
        Assert.Equal(0xd6b100, cpu.UntranslateDataAddress(0xd6b100));
    }

    [Fact]
    public void AdditionalDataAddressWindowsAreLowerPriorityThanActiveWindow()
    {
        var cpu = new Cpu(new byte[0x800], 0xd70000);
        cpu.ConfigureDataAddressWindow(0x02b100, 0x02b1ff, 0xd6b100);
        cpu.AddDataAddressWindow(0x02b180, 0x02b27f, 0xd6c180);

        Assert.Equal(0xd6b180, cpu.TranslateDataAddress(0x02b180));
        Assert.Equal(0xd6c200, cpu.TranslateDataAddress(0x02b200));
        Assert.Equal(0x02b180, cpu.UntranslateDataAddress(0xd6b180));
        Assert.Equal(0x02b200, cpu.UntranslateDataAddress(0xd6c200));
    }

    [Fact]
    public void ClearDataAddressWindowClearsAdditionalWindows()
    {
        var cpu = new Cpu(new byte[0x800], 0xd70000);
        cpu.ConfigureDataAddressWindow(0x02b100, 0x02b1ff, 0xd6b100);
        cpu.AddDataAddressWindow(0x02c100, 0x02c1ff, 0xd6c100);

        cpu.ClearDataAddressWindow();

        Assert.Equal(0x02b100, cpu.TranslateDataAddress(0x02b100));
        Assert.Equal(0x02c100, cpu.TranslateDataAddress(0x02c100));
    }

}
