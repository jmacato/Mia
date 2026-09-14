// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible fail-closed equalizer completion boundary. The proven
/// phases publish an invalid result and raise the native equalizer-done
/// source without modifying any firmware DMA buffer. Unknown configurations
/// and control transitions remain inert.
/// </summary>
internal sealed class AsicEqualizer
{
    public const int ControlAddress = 0x0920;
    public const int FirstResultAddress = 0x0923;
    public const int InvalidResultAddress = 0x0924;
    public const int LastResultAddress = 0x0929;
    public const byte ArmedControl = 0x40;
    public const byte Phase1StartControl = 0x58;
    public const byte Phase2StartControl = 0x78;
    public const byte InvalidResultMinimum = 0x8b;

    public const int Phase1InputAddress = 0x10004c;
    public const int Phase1InputLength = 0x015c;
    public const int Phase1OutputAddress = 0x101860;
    public const int Phase1OutputLength = 0x004e;

    public const int Phase2InputAddress = 0x1001d8;
    public const int Phase2MeasurementInputAddress = 0x100308;
    public const int Phase2InputLength = 0x0130;
    public const int Phase2OutputAddress = 0x1018c0;
    public const int Phase2OutputLength = 0x0072;
    public const int Phase2AuxiliaryAddress = 0x100626;
    public const int Phase2AuxiliaryLength = 8;

    // The internal equalizer latency is not yet known. One ASIC tick keeps
    // the proven asynchronous completion boundary without claiming timing.
    public const int CompletionCycles = 1;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController _interruptController;
    readonly IAsicEqualizerSource _source;
    readonly MiaWorker? _worker;
    byte _control;
    byte? _pendingPhase;

    public AsicEqualizer(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        IAsicEqualizerSource? source = null,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _source = source ?? AsicNoSignalEqualizerSource.Instance;
        _worker = worker;

        cpu.WriteHooks[ControlAddress] = WriteControl;
    }

    public long StartedCount { get; private set; }

    public long Phase1StartedCount { get; private set; }

    public long Phase2StartedCount { get; private set; }

    public long CompletedCount { get; private set; }

    bool WriteControl(byte value, byte _, int __, byte mask)
    {
        var previous = _control;
        _control = Merge(_control, value, mask);
        if (_pendingPhase is not null || previous != ArmedControl)
        {
            return false;
        }

        var phase = ResolvePhase();
        if (phase == 0)
        {
            return false;
        }

        _pendingPhase = phase;
        StartedCount++;
        Phase1StartedCount += phase == 1 ? 1 : 0;
        Phase2StartedCount += phase == 2 ? 1 : 0;
        ScheduleCompletion();
        return false;
    }

    byte ResolvePhase() => _control switch
    {
        Phase1StartControl when IsPhase1Configuration() => 1,
        Phase2StartControl when IsPhase2Configuration() => 2,
        _ => 0,
    };

    void ScheduleCompletion()
    {
        if (_worker is null)
        {
            _clock.Schedule(Complete, CompletionCycles);
            return;
        }

        _clock.Schedule(_worker, Complete, CompletionCycles);
    }

    bool IsPhase1Configuration() =>
        _cpu.Data[0x0922] == 0x4c &&
        _cpu.Data[0x0923] == 0x00 &&
        _cpu.Data[0x0924] == 0x5f &&
        _cpu.Data[0x0925] == 0x18;

    bool IsPhase2Configuration()
    {
        var inputAddress = 0x100000 |
            _cpu.Data[0x0922] |
            _cpu.Data[0x0923] << 8;
        return inputAddress is Phase2InputAddress or Phase2MeasurementInputAddress &&
            _cpu.Data[0x0924] == 0xbf &&
            _cpu.Data[0x0925] == 0x18 &&
            _cpu.Data[0x0926] == 0x23 &&
            _cpu.Data[0x0927] == 0x06;
    }

    void Complete()
    {
        var phase = _pendingPhase ?? throw new InvalidOperationException(
            "Equalizer completion has no pending phase.");
        _pendingPhase = null;

        var result = _source.Equalize(new(phase));
        _cpu.Data[0x0923] = result.Result23;
        _cpu.Data[0x0924] = result.Result24;
        _cpu.Data[0x0925] = result.Result25;
        _cpu.Data[0x0926] = result.Result26;
        _cpu.Data[0x0927] = result.Result27;
        _cpu.Data[0x0928] = result.Result28;
        _cpu.Data[0x0929] = result.Result29;
        CompletedCount++;
        _interruptController.RaiseHighPriority(
            AsicInterruptController.EqualizerDoneSource);
    }

    static byte Merge(byte oldValue, byte value, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));
}
