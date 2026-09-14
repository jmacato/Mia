// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

/// <summary>
/// Deterministic, host-independent renderer for the firmware tone generator.
/// The renderer consumes a 13 MHz ASIC-cycle timeline and emits mono signed
/// 16-bit PCM into caller-owned bounded buffers.
/// </summary>
internal sealed class AsicTonePcmRenderer
{
    public const int DefaultSampleRate = 48_000;
    public const int MaximumSampleRate = 384_000;
    public const short DefaultPeakAmplitude = 8_192;

    // The recovered divider is 26 MHz / (256 * (reload + 1)). Since the
    // machine clock is 13 MHz, one waveform period is exactly this many ASIC
    // cycles. Keeping time in 1 / SampleRate ASIC-cycle ticks makes both PCM
    // sample positions and firmware state-change positions exact integers.
    const int AsicCyclesPerSecond = MiaSystemClock.AsicCyclesPerSecond;
    const int PeriodCyclesPerReloadUnit = 128;

    readonly long _startCycle;
    long _timelineTicks;
    long _nextSampleIndex;
    long _phaseTicks;
    long _dtmfStartTicks;
    AsicToneState _currentState;

    public AsicTonePcmRenderer(
        int sampleRate = DefaultSampleRate,
        short peakAmplitude = DefaultPeakAmplitude,
        long startCycle = 0)
    {
        if (sampleRate <= 0 || sampleRate > MaximumSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (peakAmplitude <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(peakAmplitude));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(startCycle);

        SampleRate = sampleRate;
        PeakAmplitude = peakAmplitude;
        _startCycle = startCycle;
        _currentState = new(startCycle, 0, 0, 0, 0);
    }

    public int SampleRate { get; }

    public short PeakAmplitude { get; }

    /// <summary>
    /// ASIC cycle at which this renderer's PCM timeline begins. Sample zero
    /// occurs exactly at this cycle.
    /// </summary>
    public long StartCycle => _startCycle;

    public AsicToneState CurrentState => _currentState;

    /// <summary>
    /// Returns the number of samples not yet emitted before
    /// <paramref name="targetCycle"/>. A sample exactly at the target cycle is
    /// not included, so a state change at that cycle affects that sample.
    /// </summary>
    public long GetRemainingSampleCount(long targetCycle)
    {
        var targetTicks = GetTargetTicks(targetCycle);
        if (targetTicks < _timelineTicks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetCycle),
                "The tone timeline cannot run backwards.");
        }

        var sampleCountBeforeTarget = targetTicks == 0
            ? 0
            : (targetTicks - 1) / AsicCyclesPerSecond + 1;
        return sampleCountBeforeTarget - _nextSampleIndex;
    }

    /// <summary>
    /// Renders as many pending samples as fit in <paramref name="destination"/>
    /// and advances toward an ASIC-cycle boundary. Call again with the same
    /// target when <see cref="AsicTonePcmRenderResult.ReachedTarget"/> is false.
    /// </summary>
    public AsicTonePcmRenderResult RenderTo(
        long targetCycle,
        Span<short> destination)
    {
        var targetTicks = GetTargetTicks(targetCycle);
        var remaining = GetRemainingSampleCount(targetCycle);
        var samplesWritten = (int)Math.Min(remaining, destination.Length);

        for (var index = 0; index < samplesWritten; index++)
        {
            var sampleTicks = checked(
                _nextSampleIndex * AsicCyclesPerSecond);
            AdvanceTimeline(sampleTicks);
            destination[index] = RenderSample();
            _nextSampleIndex++;
        }

        var reachedTarget = samplesWritten == remaining;
        if (reachedTarget)
        {
            AdvanceTimeline(targetTicks);
        }

        return new(samplesWritten, reachedTarget);
    }

    /// <summary>
    /// Renders all samples preceding a cycle-stamped register state. The state
    /// is applied only after its cycle is reached, making same-cycle PCM use
    /// the new register values. The oscillator's normalized phase is retained
    /// when the reload value changes.
    /// </summary>
    public AsicTonePcmRenderResult RenderTo(
        AsicToneState nextState,
        Span<short> destination)
    {
        var result = RenderTo(nextState.Cycle, destination);
        if (result.ReachedTarget)
        {
            ApplyState(nextState);
        }

        return result;
    }

    void ApplyState(AsicToneState nextState)
    {
        var oldPeriodTicks = GetPeriodTicks(_currentState.Reload);
        var newPeriodTicks = GetPeriodTicks(nextState.Reload);
        if (oldPeriodTicks != newPeriodTicks)
        {
            _phaseTicks = (long)(
                (UInt128)(ulong)_phaseTicks * (ulong)newPeriodTicks /
                (ulong)oldPeriodTicks);
        }

        if (_currentState.DtmfCode != nextState.DtmfCode)
        {
            _dtmfStartTicks = _timelineTicks;
        }
        _currentState = nextState;
    }

    void AdvanceTimeline(long targetTicks)
    {
        if (targetTicks < _timelineTicks)
        {
            throw new InvalidOperationException("The tone timeline ran backwards.");
        }

        var periodTicks = GetPeriodTicks(_currentState.Reload);
        var elapsedModuloPeriod =
            (targetTicks - _timelineTicks) % periodTicks;
        _phaseTicks = (_phaseTicks + elapsedModuloPeriod) % periodTicks;
        _timelineTicks = targetTicks;
    }

    short RenderSample()
    {
        int mixed = RenderBeeperSample() + RenderDtmfSample();
        return (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
    }

    int RenderDtmfSample()
    {
        byte code = _currentState.DtmfCode;
        if (code is < 1 or > 16) return 0;
        int row = code >= 13 ? code - 13 : code <= 9 ? (code - 1) / 3 : 3;
        int column = code >= 13 ? 3 : code <= 9 ? (code - 1) % 3 : code switch
        {
            10 => 1,
            11 => 0,
            _ => 2,
        };
        int low = row switch { 0 => 697, 1 => 770, 2 => 852, _ => 941 };
        int high = column switch { 0 => 1209, 1 => 1336, 2 => 1477, _ => 1633 };
        long ticksPerSecond = (long)AsicCyclesPerSecond * SampleRate;
        long elapsed = (_timelineTicks - _dtmfStartTicks) % ticksPerSecond;
        double phase = 2 * Math.PI * elapsed / ticksPerSecond;
        return (int)Math.Round(PeakAmplitude * 0.5 *
            (Math.Sin(phase * low) + Math.Sin(phase * high)));
    }

    int RenderBeeperSample()
    {
        if (_currentState.Control == 0 || _currentState.Unknown != 0)
        {
            return 0;
        }

        // Both divider bytes are modeled as inclusive counter reloads. This is
        // provisional for Width, but agrees with the recovered narrow message
        // pulses and near-50% ringtone values while giving width zero a single
        // counter tick rather than conflating it with the control mute.
        var highTicks = checked(
            (long)PeriodCyclesPerReloadUnit *
            (_currentState.Width + 1) *
            SampleRate);
        long periodTicks = GetPeriodTicks(_currentState.Reload);
        highTicks = Math.Min(highTicks, periodTicks);
        // The speaker carries the alternating part of the pulse train. With
        // narrow key-click pulses, symmetric +/- levels introduced a large DC
        // step at every start/stop, heard as a thud. Remove the duty-dependent
        // mean while retaining the divider, pulse width, and peak-to-peak swing.
        int lowLevel = (int)(-2L * PeakAmplitude * highTicks / periodTicks);
        return _phaseTicks < highTicks ? lowLevel + 2 * PeakAmplitude : lowLevel;
    }

    long GetPeriodTicks(byte reload) => checked(
        (long)PeriodCyclesPerReloadUnit * (reload + 1) * SampleRate);

    long GetTargetTicks(long targetCycle)
    {
        if (targetCycle < _startCycle)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetCycle),
                "The target precedes the renderer's start cycle.");
        }

        return checked((targetCycle - _startCycle) * SampleRate);
    }
}
