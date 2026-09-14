// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible real-time clock calendar, seconds compare, and rollover
/// interrupt behavior. The firmware owns the calendar epoch and all alarm
/// programming through MMIO. The current model expresses one wall-clock second
/// as 13 million ASIC-reference ticks; the independent RTC oscillator remains
/// a separate clock-domain refinement.
/// </summary>
internal sealed class AsicRtc
{
    public const int SubsecondAddress = 0x0af1;
    public const int SecondAddress = 0x0af2;
    public const int MinuteAddress = 0x0af3;
    public const int HourAddress = 0x0af4;
    public const int DayAddress = 0x0af6;
    public const int MonthAddress = 0x0af7;
    public const int YearAddress = 0x0af8;
    public const int CenturyAddress = 0x0af9;
    public const int AlarmEnableAddress = 0x0afb;
    public const int InterruptEnableAddress = 0x0afc;
    public const int InterruptStatusAddress = 0x0afd;
    public const int SecondCompareAddress = 0x0b01;

    public const byte SecondCompareEnable = 0x40;
    public const byte SecondCompareInterrupt = 0x08;
    public const byte SecondRolloverInterrupt = 0x10;
    public const int SecondCycles = MiaSystemClock.AsicCyclesPerSecond;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController _interruptController;
    readonly Action _tick;
    readonly MiaWorker? _worker;

    public AsicRtc(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _tick = Tick;
        _worker = worker;

        var previousSubsecondHook = cpu.WriteHooks[SubsecondAddress];
        cpu.WriteHooks[SubsecondAddress] = (value, oldValue, address, mask) =>
        {
            ResetSecondPhase();
            return previousSubsecondHook?.Invoke(value, oldValue, address, mask) ?? false;
        };

        var previousStatusHook = cpu.ReadHooks[InterruptStatusAddress];
        cpu.ReadHooks[InterruptStatusAddress] = address =>
        {
            var value = previousStatusHook?.Invoke(address) ?? cpu.Data[address];
            cpu.Data[address] = 0;
            return value;
        };
    }

    public long TickCount { get; private set; }

    public long SecondCompareCount { get; private set; }

    public long SecondRolloverCount { get; private set; }

    void ResetSecondPhase()
    {
        _clock.Cancel(_tick);
        Schedule(_tick, SecondCycles);
    }

    void Tick()
    {
        TickCount++;
        var rolledOver = IncrementCalendar();

        if ((_cpu.Data[AlarmEnableAddress] & SecondCompareEnable) != 0 &&
            _cpu.Data[SecondAddress] == _cpu.Data[SecondCompareAddress])
        {
            SecondCompareCount++;
            LatchInterrupt(SecondCompareInterrupt);
        }

        if (rolledOver)
        {
            SecondRolloverCount++;
            LatchInterrupt(SecondRolloverInterrupt);
        }

        Schedule(_tick, SecondCycles);
    }

    void Schedule(Action callback, long delayCycles)
    {
        if (_worker is null)
        {
            _clock.Schedule(callback, delayCycles);
        }
        else
        {
            _clock.Schedule(_worker, callback, delayCycles);
        }
    }

    bool IncrementCalendar()
    {
        if (!TryFromBcd(_cpu.Data[SecondAddress], 59, out var second))
        {
            return false;
        }

        if (second < 59)
        {
            _cpu.Data[SecondAddress] = ToBcd(second + 1);
            return false;
        }

        _cpu.Data[SecondAddress] = 0;
        return IncrementMinute();
    }

    bool IncrementMinute()
    {
        if (!TryFromBcd(_cpu.Data[MinuteAddress], 59, out var minute))
        {
            return true;
        }
        if (minute < 59)
        {
            _cpu.Data[MinuteAddress] = ToBcd(minute + 1);
            return true;
        }

        _cpu.Data[MinuteAddress] = 0;
        return IncrementHour();
    }

    bool IncrementHour()
    {
        if (!TryFromBcd(_cpu.Data[HourAddress], 23, out var hour))
        {
            return true;
        }
        if (hour < 23)
        {
            _cpu.Data[HourAddress] = ToBcd(hour + 1);
            return true;
        }

        _cpu.Data[HourAddress] = 0;
        IncrementDate();
        return true;
    }

    void IncrementDate()
    {
        if (!TryReadDate(out var date))
        {
            return;
        }

        IncrementValidDate(date);
    }

    bool TryReadDate(out AsicRtcDate date)
    {
        date = default;
        if (!TryFromBcd(_cpu.Data[DayAddress], 31, out var day) || day == 0 ||
            !TryFromBcd(_cpu.Data[MonthAddress], 12, out var month) || month == 0 ||
            !TryFromBcd(_cpu.Data[YearAddress], 99, out var year) ||
            !TryFromBcd(_cpu.Data[CenturyAddress], 99, out var century))
        {
            return false;
        }

        var fullYear = century * 100 + year;
        date = new(day, month, year, century, fullYear);
        return fullYear is >= 1 and <= 9999;
    }

    void IncrementValidDate(AsicRtcDate date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.FullYear, date.Month);
        if (date.Day < daysInMonth)
        {
            _cpu.Data[DayAddress] = ToBcd(date.Day + 1);
            return;
        }

        _cpu.Data[DayAddress] = 0x01;
        if (date.Month < 12)
        {
            _cpu.Data[MonthAddress] = ToBcd(date.Month + 1);
            return;
        }

        _cpu.Data[MonthAddress] = 0x01;
        if (date.Year < 99)
        {
            _cpu.Data[YearAddress] = ToBcd(date.Year + 1);
            return;
        }

        _cpu.Data[YearAddress] = 0;
        _cpu.Data[CenturyAddress] = ToBcd((date.Century + 1) % 100);
    }

    void LatchInterrupt(byte interrupt)
    {
        _cpu.Data[InterruptStatusAddress] |= interrupt;
        if ((_cpu.Data[InterruptEnableAddress] & interrupt) != 0)
        {
            _interruptController.RaiseHighPriority(AsicInterruptController.RtcSource);
        }
    }

    static bool TryFromBcd(byte value, int maximum, out int result)
    {
        var high = value >> 4;
        var low = value & 0x0f;
        result = high * 10 + low;
        return high <= 9 && low <= 9 && result <= maximum;
    }

    static byte ToBcd(int value) =>
        (byte)((value / 10 << 4) | value % 10);
}
