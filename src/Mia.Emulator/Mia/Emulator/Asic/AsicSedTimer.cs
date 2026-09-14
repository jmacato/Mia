// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Free-running ASIC scheduler timer. Firmware enables the high-priority
/// interrupt domain through bit 5 of 0x0810; each hardware tick then enters
/// the registered SED_Timer callback through logical source 0x18.
/// </summary>
internal sealed class AsicSedTimer
{
    public const int InterruptControlAddress = 0x0810;
    public const byte HighPriorityEnable = 0x20;

    // One GSM frame at the handset's 13 MHz ASIC reference (4.615 ms, rounded
    // to the integer cadence used by the ASIC-facing firmware).
    public const int TickCycles = 60_000;

    readonly MiaSystemClock _clock;
    readonly AsicInterruptController _interruptController;
    readonly Action _tick;
    readonly MiaWorker? _worker;
    bool _scheduled;

    public AsicSedTimer(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        MiaWorker? worker = null)
    {
        _clock = clock;
        _interruptController = interruptController;
        _tick = Tick;
        _worker = worker;

        var previousHook = cpu.WriteHooks[InterruptControlAddress];
        cpu.WriteHooks[InterruptControlAddress] = (value, oldValue, address, mask) =>
        {
            var newValue = (byte)((oldValue & ~mask) | (value & mask));
            SetEnabled((newValue & HighPriorityEnable) != 0);
            return previousHook?.Invoke(value, oldValue, address, mask) ?? false;
        };

        SetEnabled((cpu.Data[InterruptControlAddress] & HighPriorityEnable) != 0);
    }

    public bool Enabled { get; private set; }

    public long TickCount { get; private set; }

    void SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
        {
            return;
        }

        Enabled = enabled;
        if (enabled)
        {
            ScheduleTick();
        }
        else if (_scheduled)
        {
            _clock.Cancel(_tick);
            _scheduled = false;
        }
    }

    void ScheduleTick()
    {
        if (_scheduled || !Enabled)
        {
            return;
        }

        if (_worker is null)
        {
            _clock.Schedule(_tick, TickCycles);
        }
        else
        {
            _clock.Schedule(_worker, _tick, TickCycles);
        }
        _scheduled = true;
    }

    void Tick()
    {
        _scheduled = false;
        if (!Enabled)
        {
            return;
        }

        TickCount++;
        _interruptController.RaiseHighPriority(AsicInterruptController.SedTimerSource);
        ScheduleTick();
    }
}
