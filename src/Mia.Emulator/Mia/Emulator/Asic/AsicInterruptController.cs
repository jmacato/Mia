// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AvrCore;

namespace Mia.Emulator.Asic;

internal sealed class AsicInterruptController
{
    public const int External1Source = 0x04;
    public const int KeypadSource = 0x06;
    public const int I2c1Source = 0x07;
    public const int RtcSource = 0x08;
    public const int SimRxLowSource = 0x09;
    public const int LlcHardwareSource = 0x0a;
    public const int LinkReceiveSource = 0x0d;
    public const int LinkTransmitSource = 0x0e;
    public const int SimTransmitSource = 0x10;
    public const int SimRxSource = 0x22;
    public const int ChannelDecoderDoneSource = 0x23;
    public const int ChannelEncoderDoneSource = 0x24;
    public const int EqualizerDoneSource = 0x25;
    public const int I2cSource = 0x17;
    public const int SedTimerSource = 0x18;
    public const int PhProcessSource = 0x19;
    public const int PhDispatcherSource = 0x1a;
    public const int TimeGeneratorAdcSource = 0x1b;
    public const int TimeGeneratorSc2Source = 0x1c;
    public const int TimeGeneratorSc1Source = 0x1d;
    public const int TimeGeneratorSc0Source = 0x1e;
    public const int AdcDoneSource = 0x27;
    public const int FrameTickSource = 0x28;
    public const int HighPriorityVectorWord = 0x30;
    public const int SourceRegister = 0x0816;

    readonly Cpu _cpu;
    readonly MiaWorker? _worker;
    AsicInterruptControllerState _state = AsicInterruptControllerState.Empty;
    readonly long[] _raisedBySource = new long[byte.MaxValue + 1];
    long _raisedCount;
    long _dispatchedCount;
    long _returnedCount;
    readonly AvrInterruptConfig _highPriorityInterrupt = new()
    {
        Address = HighPriorityVectorWord,
        FlagRegister = 0,
        FlagMask = 0,
    };

    public AsicInterruptController(Cpu cpu, MiaWorker? worker = null)
    {
        _cpu = cpu;
        _worker = worker;
        cpu.GlobalInterruptEnableChanged += OnGlobalInterruptEnableChanged;
    }

    public long RaisedCount => Interlocked.Read(ref _raisedCount);
    public long DispatchedCount => Interlocked.Read(ref _dispatchedCount);
    public long ReturnedCount => Interlocked.Read(ref _returnedCount);

    internal bool IsQuiescentForIdleLoop
    {
        get
        {
            AsicInterruptControllerState state = Volatile.Read(ref _state);
            return state.PendingSources.IsEmpty &&
                state.ActiveSources.IsEmpty &&
                state.PresentedSource is null &&
                !state.ActivationNeeded &&
                _cpu.NextInterrupt < 0;
        }
    }

    public long GetRaisedCount(byte source)
        => Volatile.Read(ref _raisedBySource[source]);

    public static string GetSourceName(byte source) => source switch
    {
        External1Source => "EXT_1",
        KeypadSource => "KEYB_INT",
        I2c1Source => "I2C1_INT",
        RtcSource => "RTC",
        SimRxLowSource => "SIMRX_LOW",
        LlcHardwareSource => "LLC_HW_INT",
        LinkReceiveSource => "LH_RX",
        LinkTransmitSource => "LH_TX",
        SimTransmitSource => "SIMTX",
        SimRxSource => "SIMRX",
        ChannelDecoderDoneSource => "HW_ChannelDecoder_Done",
        ChannelEncoderDoneSource => "HW_ChannelEncoder_Done",
        EqualizerDoneSource => "HW_Equalizer_Done",
        I2cSource => "I2C_INT",
        SedTimerSource => "SED_Timer",
        PhProcessSource => "PH_Process",
        PhDispatcherSource => "PH_DispatcherProcess",
        TimeGeneratorAdcSource => "HW_TG_ADC",
        TimeGeneratorSc2Source => "HW_TG_SC_2",
        TimeGeneratorSc1Source => "HW_TG_SC_1",
        TimeGeneratorSc0Source => "HW_TG_SC_0",
        AdcDoneSource => "HW_ADC_Done",
        FrameTickSource => "FrameTick_High_Priority",
        _ => $"logical_{source:x2}",
    };

    public void RaiseHighPriority(byte source)
    {
        var (previous, next) = UpdateState(state => RaiseHighPriorityCore(state, source));
        ApplyActivation(previous, next);
        RecordRaised(source);
    }

    public void RaiseHighPriorityPair(byte firstSource, byte secondSource)
    {
        var (previous, next) = UpdateState(state =>
            RaiseHighPriorityCore(RaiseHighPriorityCore(state, firstSource), secondSource));
        ApplyActivation(previous, next);
        RecordRaised(firstSource);
        RecordRaised(secondSource);
    }

    void RecordRaised(byte source)
    {
        Interlocked.Increment(ref _raisedBySource[source]);
        Interlocked.Increment(ref _raisedCount);
    }

    AsicInterruptControllerState RaiseHighPriorityCore(AsicInterruptControllerState state, byte source)
    {
        if (state.QueuedSources.Contains(source))
        {
            return state;
        }
        return ActivateNext(state with
        {
            PendingSources = state.PendingSources.Add(source),
            QueuedSources = state.QueuedSources.Add(source),
        });
    }

    public void NotifyHighPriorityDispatched()
    {
        var (previous, next) = UpdateState(NotifyHighPriorityDispatchedCore);
        ApplyActivation(previous, next);
        Interlocked.Increment(ref _dispatchedCount);
    }

    AsicInterruptControllerState NotifyHighPriorityDispatchedCore(AsicInterruptControllerState state)
    {
        if (state.PresentedSource is not { } presented)
        {
            return state;
        }
        return ActivateNext(state with
        {
            PendingSources = state.PendingSources.Remove(presented),
            QueuedSources = state.QueuedSources.Remove(presented),
            ActiveSources = state.ActiveSources.Push(presented),
            PresentedSource = null,
        });
    }

    public void NotifyHighPriorityReturned()
    {
        var (previous, next) = UpdateState(NotifyHighPriorityReturnedCore);
        ApplyActivation(previous, next);
        Interlocked.Increment(ref _returnedCount);
    }

    AsicInterruptControllerState NotifyHighPriorityReturnedCore(AsicInterruptControllerState state)
    {
        if (state.ActiveSources.IsEmpty)
        {
            return ActivateNext(state);
        }
        return ActivateNext(state with { ActiveSources = state.ActiveSources.Pop() });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareForTick()
    {
        var (previous, next) = UpdateState(PrepareForTickCore);
        ApplyActivation(previous, next);
    }

    AsicInterruptControllerState PrepareForTickCore(AsicInterruptControllerState state)
    {
        if (state.PresentedSource is not null || state.PendingSources.IsEmpty)
        {
            return state;
        }
        return ActivateNext(state);
    }

    void OnGlobalInterruptEnableChanged(
        object? sender,
        CpuGlobalInterruptEnableChangedEventArgs args)
    {
        if (!args.Enabled || !Volatile.Read(ref _state).ActivationNeeded)
        {
            return;
        }
        var (previous, next) = UpdateState(ActivateNext);
        ApplyActivation(previous, next);
    }

    // Pure state transform: never touches _cpu.WriteData/QueueInterrupt, so it
    // is safe to re-run on every CAS retry attempt without double-dispatching
    // an interrupt. ApplyActivation performs that side effect exactly once,
    // after a successful CAS, by comparing the presented source before/after.
    AsicInterruptControllerState ActivateNext(AsicInterruptControllerState state)
    {
        if (state.PresentedSource is not null ||
            _cpu.NextInterrupt == HighPriorityVectorWord)
        {
            return state.ActivationNeeded ? state with { ActivationNeeded = false } : state;
        }
        if (!state.ActiveSources.IsEmpty && !_cpu.InterruptsEnabled)
        {
            bool activationNeeded = HasEligiblePendingSource(state);
            return state.ActivationNeeded == activationNeeded
                ? state
                : state with { ActivationNeeded = activationNeeded };
        }

        byte? pending = FindEligiblePendingSource(state);
        if (pending is null)
        {
            return state.ActivationNeeded ? state with { ActivationNeeded = false } : state;
        }

        return state with { ActivationNeeded = false, PresentedSource = pending };
    }

    void ApplyActivation(AsicInterruptControllerState previous, AsicInterruptControllerState next)
    {
        if (next.PresentedSource is { } presented && previous.PresentedSource != presented)
        {
            _cpu.WriteData(SourceRegister, presented);
            _cpu.QueueInterrupt(_highPriorityInterrupt);
        }
    }

    static bool HasEligiblePendingSource(AsicInterruptControllerState state) =>
        FindEligiblePendingSource(state) is not null;

    static byte? FindEligiblePendingSource(AsicInterruptControllerState state)
    {
        foreach (byte candidate in state.PendingSources)
        {
            if (!state.ActiveSources.Contains(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    (AsicInterruptControllerState Previous, AsicInterruptControllerState Next) UpdateState(
        Func<AsicInterruptControllerState, AsicInterruptControllerState> transform)
    {
        while (true)
        {
            AsicInterruptControllerState previous = Volatile.Read(ref _state);
            AsicInterruptControllerState next = transform(previous);
            if (ReferenceEquals(next, previous))
            {
                return (previous, previous);
            }
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _state, next, previous),
                previous))
            {
                return (previous, next);
            }
        }
    }
}
