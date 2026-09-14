// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class AsicKeypadTests
{
    [Fact]
    public void ArmingDetectionReportsAnAlreadyHeldPowerKeyOnce()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu,
            pressedScanMask: AsicKeypad.NoPowerScanMask,
            pressedRowMask: AsicKeypad.NoPowerRowMask);
        int interrupts = 0;
        keypad.InterruptRequested += () => interrupts++;

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0f);
        Assert.Equal(0, interrupts);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1f);
        Assert.Equal(1, interrupts);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1f);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1e);
        Assert.Equal(1, interrupts);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0f);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1f);
        Assert.Equal(2, interrupts);
        keypad.Release();
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0f);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1f);
        Assert.Equal(2, interrupts);
    }

    [Fact]
    public void ArmingDetectionWithoutAContactDoesNotRaiseAnInterrupt()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu);
        keypad.InterruptRequested += () => Assert.Fail("No key is held");

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1f);

        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void MaskedScanWritesPreserveTheInterruptEnableBit()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu);
        keypad.Press(0x07, 0x01);
        int interrupts = 0;
        keypad.InterruptRequested += () => interrupts++;

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x1f, 0x0f);
        Assert.Equal(0x0f, cpu.Data[AsicKeypad.ScanControlAddress]);
        Assert.Equal(0, interrupts);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x10, 0x10);
        Assert.Equal(0x1f, cpu.Data[AsicKeypad.ScanControlAddress]);
        Assert.Equal(1, interrupts);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x07, 0x0f);
        Assert.Equal(0x17, cpu.Data[AsicKeypad.ScanControlAddress]);
        Assert.Equal(1, interrupts);
        Assert.Equal(0x1e, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void ReportsTheFirmwareProvenActiveLowIdleRows()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        _ = new AsicKeypad(cpu);

        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void GroundsOnlyTheConfiguredRowForTheSelectedScanMask()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu, pressedScanMask: 0x0d, pressedRowMask: 0x04);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0e);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0d);
        Assert.Equal(0x1b, cpu.ReadData(AsicKeypad.RowStateAddress));

        keypad.Release();
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void SupportsAnInitiallyReleasedScheduledPress()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(
            cpu,
            pressedScanMask: 0x0f,
            pressedRowMask: 0x10,
            pressedInitially: false);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0f);

        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
        keypad.Press();
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void ConfiguredChordGroundsTheRowForBothScanColumns()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(
            cpu,
            pressedScanMask: 0x0b,
            secondaryPressedScanMask: 0x07,
            pressedRowMask: 0x10);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x07);
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0e);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));

        keypad.Release();
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void LiveChordGroundsAndReleasesBothScanColumnsAtomically()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu);
        keypad.Press(0x0e, 0x10, secondaryScanMask: 0x0d);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0e);
        Assert.Equal(
            0x0f,
            cpu.ReadData(AsicKeypad.RowStateAddress));
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0d);
        Assert.Equal(
            0x0f,
            cpu.ReadData(AsicKeypad.RowStateAddress));

        keypad.Release(0x0e, 0x10, secondaryScanMask: 0x0d);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void SupportsIndependentLiveMatrixContacts()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu);

        keypad.Press(0x0b, 0x02);
        keypad.Press(0x0b, 0x08);
        keypad.Press(0x07, 0x01);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(0x15, cpu.ReadData(AsicKeypad.RowStateAddress));

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x07);
        Assert.Equal(0x1e, cpu.ReadData(AsicKeypad.RowStateAddress));

        keypad.Release(0x0b, 0x02);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(0x17, cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void OverlappingChordsKeepTheirSharedContactUntilBothRelease()
    {
        var cpu = new Cpu(new byte[2], 0x10000);
        var keypad = new AsicKeypad(cpu);
        keypad.Press(0x0b, 0x10, secondaryScanMask: 0x07);
        keypad.Press(0x0e, 0x10, secondaryScanMask: 0x07);

        keypad.Release(0x0b, 0x10, secondaryScanMask: 0x07);

        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x07);
        Assert.Equal(0x0f, cpu.ReadData(AsicKeypad.RowStateAddress));
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0b);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));

        keypad.Release(0x0e, 0x10, secondaryScanMask: 0x07);
        cpu.WriteData(AsicKeypad.ScanControlAddress, 0x07);
        Assert.Equal(AsicKeypad.IdleRows, cpu.ReadData(AsicKeypad.RowStateAddress));
    }
}
