// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

/// <summary>
/// Frontend pacing utility that maps deterministic guest cycles onto host
/// monotonic time. It does not provide guest time and is not part of any
/// firmware-visible peripheral model.
/// </summary>
internal sealed class RealTimePacer
{
    readonly long _cyclesPerSecond;
    readonly IRealTimePacerClock _hostClock;
    readonly long? _maximumCatchUpDebtTicks;
    long _anchorCycles;
    long _anchorTimestamp;

    public RealTimePacer(
        long cyclesPerSecond = MiaSystemClock.AsicCyclesPerSecond,
        TimeSpan? maximumCatchUpDebt = null)
        : this(cyclesPerSecond, SystemRealTimePacerClock.Instance, maximumCatchUpDebt)
    {
    }

    internal RealTimePacer(
        long cyclesPerSecond,
        IRealTimePacerClock hostClock,
        TimeSpan? maximumCatchUpDebt = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cyclesPerSecond);
        ArgumentNullException.ThrowIfNull(hostClock);
        if (hostClock.Frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostClock));
        }
        if (maximumCatchUpDebt is { } catchUpDebt && catchUpDebt <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCatchUpDebt));
        }
        _cyclesPerSecond = cyclesPerSecond;
        _hostClock = hostClock;
        _maximumCatchUpDebtTicks = maximumCatchUpDebt is { } limit
            ? Math.Max(1, (long)Math.Round(limit.TotalSeconds * hostClock.Frequency))
            : null;
        Reanchor(0);
    }

    public double DriftMilliseconds { get; private set; }

    public void Reanchor(long cycles)
    {
        var now = _hostClock.GetTimestamp();
        _anchorCycles = cycles;
        _anchorTimestamp = now;
        DriftMilliseconds = 0;
    }

    public void Pace(long cycles, CancellationToken cancellationToken)
    {
        var target = GetTargetTimestamp(cycles);
        var remaining = target - _hostClock.GetTimestamp();
        if (remaining > 0)
        {
            var waitMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(remaining * 1000.0 / _hostClock.Frequency));
            _hostClock.Wait(waitMilliseconds, cancellationToken);
        }

        UpdateDrift(cycles, target, _hostClock.GetTimestamp());
    }

    public TimeSpan GetDelay(long cycles)
    {
        var target = GetTargetTimestamp(cycles);
        var now = _hostClock.GetTimestamp();
        if (UpdateDrift(cycles, target, now))
        {
            return TimeSpan.Zero;
        }

        var remaining = target - now;
        return remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                remaining / (double)_hostClock.Frequency);
    }

    public async ValueTask PaceAsync(long cycles, CancellationToken cancellationToken)
    {
        var target = GetTargetTimestamp(cycles);
        var delayed = false;
        var remaining = target - _hostClock.GetTimestamp();
        if (remaining > 0)
        {
            var waitMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(
                    remaining * 1000.0 / _hostClock.Frequency));
            await _hostClock.DelayAsync(waitMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            delayed = true;
        }

        var completedAt = _hostClock.GetTimestamp();
        if (UpdateDrift(cycles, target, completedAt))
        {
            await _hostClock.YieldAsync().ConfigureAwait(false);
            return;
        }
        if (delayed)
        {
            return;
        }

        // Browser execution must periodically yield to paint and accept input,
        // but lateness is never converted into another timed delay. The next
        // batch therefore continues catching up against the original anchor.
        await _hostClock.YieldAsync().ConfigureAwait(false);
    }

    long GetTargetTimestamp(long cycles)
    {
        var elapsedCycles = cycles - _anchorCycles;
        var elapsedTimestampTicks =
            elapsedCycles * (double)_hostClock.Frequency / _cyclesPerSecond;
        return _anchorTimestamp + (long)Math.Round(elapsedTimestampTicks);
    }

    bool UpdateDrift(long cycles, long target, long now)
    {
        DriftMilliseconds = (now - target) * 1000.0 / _hostClock.Frequency;
        if (_maximumCatchUpDebtTicks is { } maximumDebt &&
            now - target > maximumDebt)
        {
            Reanchor(cycles);
            return true;
        }
        return false;
    }
}
