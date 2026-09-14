// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicRtcTests
{
    [Fact]
    public void BlankRtcDoesNotRunBeforeFirmwareResetsTheSubsecondPhase()
    {
        var (cpu, clock, _, rtc) = CreateRtc();

        clock.AdvanceBy(AsicRtc.SecondCycles * 2L);

        Assert.Equal(0, rtc.TickCount);
        Assert.Equal(0, cpu.Data[AsicRtc.SecondAddress]);
    }

    [Fact]
    public void FirmwareSubsecondWriteStartsOneSecondPhase()
    {
        var (cpu, clock, _, rtc) = CreateRtc();
        SetCalendar(cpu, new(
            Second: 0x58,
            Minute: 0x34,
            Hour: 0x12,
            Day: 0x14,
            Month: 0x07,
            Year: 0x26,
            Century: 0x20));

        cpu.WriteData(AsicRtc.SubsecondAddress, 0);
        clock.AdvanceBy(AsicRtc.SecondCycles - 1);
        Assert.Equal(0x58, cpu.Data[AsicRtc.SecondAddress]);

        clock.AdvanceBy(1);
        Assert.Equal(0x59, cpu.Data[AsicRtc.SecondAddress]);
        Assert.Equal(1, rtc.TickCount);
    }

    [Fact]
    public void CalendarCarriesAcrossLeapDay()
    {
        var (cpu, clock, _, _) = CreateRtc();
        SetCalendar(cpu, new(
            Second: 0x59,
            Minute: 0x59,
            Hour: 0x23,
            Day: 0x28,
            Month: 0x02,
            Year: 0x00,
            Century: 0x20));
        cpu.WriteData(AsicRtc.SubsecondAddress, 0);

        clock.AdvanceBy(AsicRtc.SecondCycles);

        Assert.Equal(0x00, cpu.Data[AsicRtc.SecondAddress]);
        Assert.Equal(0x00, cpu.Data[AsicRtc.MinuteAddress]);
        Assert.Equal(0x00, cpu.Data[AsicRtc.HourAddress]);
        Assert.Equal(0x29, cpu.Data[AsicRtc.DayAddress]);
        Assert.Equal(0x02, cpu.Data[AsicRtc.MonthAddress]);
        Assert.Equal(0x00, cpu.Data[AsicRtc.YearAddress]);
        Assert.Equal(0x20, cpu.Data[AsicRtc.CenturyAddress]);
    }

    [Fact]
    public void EnabledSecondCompareLatchesStatusAndRaisesRtcSource()
    {
        var (cpu, clock, controller, rtc) = CreateRtc();
        SetCalendar(cpu, new(
            Second: 0x58,
            Minute: 0,
            Hour: 0,
            Day: 1,
            Month: 1,
            Year: 0,
            Century: 0x20));
        cpu.Data[AsicRtc.SecondCompareAddress] = 0x59;
        cpu.Data[AsicRtc.AlarmEnableAddress] = AsicRtc.SecondCompareEnable;
        cpu.Data[AsicRtc.InterruptEnableAddress] = AsicRtc.SecondCompareInterrupt;
        cpu.WriteData(AsicRtc.SubsecondAddress, 0);

        clock.AdvanceBy(AsicRtc.SecondCycles);

        Assert.Equal(1, rtc.SecondCompareCount);
        Assert.Equal(1, controller.RaisedCount);
        Assert.Equal(AsicInterruptController.RtcSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicRtc.SecondCompareInterrupt,
            cpu.ReadData(AsicRtc.InterruptStatusAddress));
        Assert.Equal(0, cpu.ReadData(AsicRtc.InterruptStatusAddress));
    }

    [Fact]
    public void SecondsRolloverUsesSeparateFirmwareEnableAndStatusBit()
    {
        var (cpu, clock, controller, rtc) = CreateRtc();
        SetCalendar(cpu, new(
            Second: 0x59,
            Minute: 0x12,
            Hour: 0,
            Day: 1,
            Month: 1,
            Year: 0,
            Century: 0x20));
        cpu.Data[AsicRtc.InterruptEnableAddress] = AsicRtc.SecondRolloverInterrupt;
        cpu.WriteData(AsicRtc.SubsecondAddress, 0);

        clock.AdvanceBy(AsicRtc.SecondCycles);

        Assert.Equal(0x00, cpu.Data[AsicRtc.SecondAddress]);
        Assert.Equal(0x13, cpu.Data[AsicRtc.MinuteAddress]);
        Assert.Equal(1, rtc.SecondRolloverCount);
        Assert.Equal(1, controller.RaisedCount);
        Assert.Equal(AsicRtc.SecondRolloverInterrupt,
            cpu.ReadData(AsicRtc.InterruptStatusAddress));
    }

    static (
        Cpu Cpu,
        MiaSystemClock Clock,
        AsicInterruptController Controller,
        AsicRtc Rtc) CreateRtc()
    {
        var cpu = new Cpu(new byte[0x800000], 0x2000);
        var clock = new MiaSystemClock();
        var controller = new AsicInterruptController(cpu);
        return (cpu, clock, controller, new AsicRtc(cpu, clock, controller));
    }

    static void SetCalendar(Cpu cpu, RtcCalendar calendar)
    {
        cpu.Data[AsicRtc.SecondAddress] = calendar.Second;
        cpu.Data[AsicRtc.MinuteAddress] = calendar.Minute;
        cpu.Data[AsicRtc.HourAddress] = calendar.Hour;
        cpu.Data[AsicRtc.DayAddress] = calendar.Day;
        cpu.Data[AsicRtc.MonthAddress] = calendar.Month;
        cpu.Data[AsicRtc.YearAddress] = calendar.Year;
        cpu.Data[AsicRtc.CenturyAddress] = calendar.Century;
    }

}
