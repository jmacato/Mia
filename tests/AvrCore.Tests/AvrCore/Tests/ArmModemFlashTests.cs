// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemFlashTests
{
    [Fact]
    public void HalfwordCodeFetchReadsMirroredFlashAndPreservesIdentifierMode()
    {
        var flash = new ArmModemFlash([0x12, 0x34, 0x56, 0x78], ArmModemFlashProfile.StM36Dr216C);
        Assert.True(flash.TryReadCodeHalf(ArmModemFlash.BaseAddress, out uint first));
        Assert.Equal(0x3412u, first);
        Assert.True(flash.TryReadCodeHalf(ArmModemFlash.BaseAddress + flash.Profile.Size + 2, out uint mirrored));
        Assert.Equal(0x7856u, mirrored);
        Assert.False(flash.TryReadCodeHalf(ArmModemFlash.BaseAddress - 2, out _));
        Assert.False(flash.TryReadCodeHalf(ArmModemFlash.BaseAddress + ArmModemFlash.WindowSize, out _));

        flash.TryWrite(0x013f0aaa, 0xaa, 1);
        flash.TryWrite(0x013f0555, 0x55, 1);
        flash.TryWrite(0x013f0aaa, 0x90, 1);
        Assert.False(flash.TryReadCodeHalf(0x013f0000, out _));
        Assert.False(flash.TryReadCodeHalf(0x013f0002, out _));
        Assert.True(flash.TryRead(0x013f0000, 2, out uint manufacturer));
        Assert.Equal(0x20u, manufacturer);
    }

    [Fact]
    public void FirmwareCommandSequenceReadsConfiguredJedecIdentity()
    {
        var flash = new ArmModemFlash([0x12, 0x34], ArmModemFlashProfile.StM36Dr216C);

        Assert.True(flash.TryWrite(0x013f0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(0x013f0555, 0x55, 1));
        Assert.True(flash.TryWrite(0x013f0aaa, 0x90, 1));
        Assert.True(flash.TryRead(0x013f0000, 2, out uint manufacturer));
        Assert.True(flash.TryRead(0x013f0002, 2, out uint device));

        Assert.Equal(0x0020u, manufacturer);
        Assert.Equal(0x0093u, device);
        Assert.True(flash.TryWrite(0x013f0000, 0xf0, 1));
    }

    [Fact]
    public void UnlockBypassProgrammingOnlyClearsFlashBits()
    {
        var flash = new ArmModemFlash([], ArmModemFlashProfile.StM36Dr216C);
        const uint bank = 0x011d0000;

        Assert.True(flash.TryWrite(bank + 0x0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(bank + 0x0554, 0x55, 1));
        Assert.True(flash.TryWrite(bank + 0x0aaa, 0x20, 1));
        Assert.True(flash.TryWrite(bank, 0xa0, 2));
        Assert.True(flash.TryWrite(bank, 0xfffe, 2));
        Assert.True(flash.TryRead(bank, 2, out uint programmed));

        Assert.Equal(0xfffeu, programmed);

        Assert.True(flash.TryWrite(bank, 0xa0, 2));
        Assert.True(flash.TryWrite(bank, 0xffff, 2));
        Assert.True(flash.TryRead(bank, 2, out programmed));
        Assert.Equal(0xfffeu, programmed);
    }

    [Fact]
    public void SixCycleEraseRestoresOnlyTheAddressedParameterBlock()
    {
        var flash = new ArmModemFlash([], ArmModemFlashProfile.StM36Dr216C);
        const uint erasedBank = 0x011d0000;
        const uint adjacentBank = 0x011e0000;
        ProgramWordInBypassMode(flash, erasedBank, 0xfffa);
        ProgramWordInBypassMode(flash, adjacentBank, 0xfffe);

        Assert.True(flash.TryWrite(erasedBank + 0x0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(erasedBank + 0x0554, 0x55, 1));
        Assert.True(flash.TryWrite(erasedBank + 0x0aaa, 0x80, 1));
        Assert.True(flash.TryWrite(erasedBank + 0x0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(erasedBank + 0x0554, 0x55, 1));
        Assert.True(flash.TryWrite(erasedBank, 0x30, 1));
        Assert.True(flash.TryRead(erasedBank, 1, out uint eraseStatus));
        Assert.True(flash.TryRead(erasedBank, 2, out uint erased));
        Assert.True(flash.TryRead(adjacentBank, 2, out uint adjacent));

        Assert.Equal(0x08u, eraseStatus);
        Assert.Equal(0xffffu, erased);
        Assert.Equal(0xfffeu, adjacent);
    }

    [Fact]
    public void EraseSuspendReportsStableDq6AndResumeCompletesTheBlock()
    {
        var flash = new ArmModemFlash([], ArmModemFlashProfile.StM36Dr216C);
        const uint bank = 0x011d0000;
        ProgramWordInBypassMode(flash, bank, 0xfffa);
        Assert.True(flash.TryWrite(bank + 0x0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(bank + 0x0554, 0x55, 1));
        Assert.True(flash.TryWrite(bank + 0x0aaa, 0x80, 1));
        Assert.True(flash.TryWrite(bank + 0x0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(bank + 0x0554, 0x55, 1));
        Assert.True(flash.TryWrite(bank, 0x30, 1));
        Assert.True(flash.TryRead(bank, 1, out uint eraseStatus));

        Assert.Equal(0x08u, eraseStatus);
        Assert.True(flash.TryWrite(bank, 0xb0, 1));
        Assert.True(flash.TryRead(bank, 2, out uint suspendedStatus1));
        Assert.True(flash.TryRead(bank, 2, out uint suspendedStatus2));
        Assert.Equal(0x4040u, suspendedStatus1);
        Assert.Equal(suspendedStatus1, suspendedStatus2);
        Assert.True(flash.TryWrite(bank, 0x30, 1));
        Assert.True(flash.TryRead(bank, 2, out uint erased));
        Assert.Equal(0xffffu, erased);
    }

    [Fact]
    public void RejectsFirmwareLargerThanConfiguredPart()
    {
        byte[] firmware = new byte[ArmModemFlashProfile.StM36Dr216C.Size + 1];

        Assert.Throws<ArgumentException>(
            () => new ArmModemFlash(firmware, ArmModemFlashProfile.StM36Dr216C));
    }

    static void ProgramWordInBypassMode(ArmModemFlash flash, uint bank, ushort value)
    {
        Assert.True(flash.TryWrite(bank + 0x0aaa, 0xaa, 1));
        Assert.True(flash.TryWrite(bank + 0x0554, 0x55, 1));
        Assert.True(flash.TryWrite(bank + 0x0aaa, 0x20, 1));
        Assert.True(flash.TryWrite(bank, 0xa0, 2));
        Assert.True(flash.TryWrite(bank, value, 2));
        Assert.True(flash.TryWrite(bank, 0x90, 1));
        Assert.True(flash.TryWrite(bank, 0x00, 1));
    }
}
