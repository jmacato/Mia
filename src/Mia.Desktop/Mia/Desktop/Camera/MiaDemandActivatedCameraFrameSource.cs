// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Mia.Desktop.Camera;

/// <summary>
/// Keeps a platform camera closed until the emulated accessory requests a
/// frame. Repeated requests keep capture active; an idle period releases it.
/// </summary>
internal sealed class MiaDemandActivatedCameraFrameSource :
    IMiaCameraFrameSource
{
    static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(2);

    readonly Channel<byte> _transitionGate = CreateTransitionGate();
    readonly CancellationTokenSource _lifetime = new();
    readonly TimeSpan _idleTimeout;
    IMiaCameraFrameSource? _source;
    Exception? _activationError;
    long _lastDemandTimestamp;
    long _demandVersion;
    int _active;
    int _activationScheduled;
    int _disposed;
    int _idleMonitorScheduled;

    public MiaDemandActivatedCameraFrameSource(
        IMiaCameraFrameSource source,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        TimeSpan timeout = idleTimeout ?? DefaultIdleTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }
        _source = source;
        _idleTimeout = timeout;
    }

    public event Action<Exception>? BackendFailed;

    public string DisplayName => Volatile.Read(ref _source)?.DisplayName ??
        "Host camera";

    public bool IsAvailable => Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _activationError) is null &&
        Volatile.Read(ref _source)?.IsAvailable == true;

    public string? UnavailableReason =>
        Volatile.Read(ref _activationError)?.Message ??
        Volatile.Read(ref _source)?.UnavailableReason;

    public async ValueTask StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        RegisterDemand();
        while (Volatile.Read(ref _active) == 0)
        {
            if (Volatile.Read(ref _activationError) is Exception error)
            {
                throw new InvalidOperationException(
                    "The host camera could not start.",
                    error);
            }
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool TryGetLatestFrame(out MiaCameraFrame? frame)
    {
        frame = null;
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _activationError) is not null)
        {
            return false;
        }

        RegisterDemand();
        IMiaCameraFrameSource? source = Volatile.Read(ref _source);
        return Volatile.Read(ref _active) != 0 && source is not null &&
            source.TryGetLatestFrame(out frame);
    }

    public async ValueTask StopAsync()
    {
        _ = Interlocked.Increment(ref _demandVersion);
        Volatile.Write(ref _lastDemandTimestamp, 0);
        _ = await _transitionGate.Reader.ReadAsync().ConfigureAwait(false);
        try
        {
            await StopActiveSourceAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Writer.TryWrite(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = Interlocked.Increment(ref _demandVersion);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _ = await _transitionGate.Reader.ReadAsync().ConfigureAwait(false);
        try
        {
            await DisposeSourceAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Writer.TryWrite(0);
            _lifetime.Dispose();
        }
    }

    async ValueTask DisposeSourceAsync()
    {
        IMiaCameraFrameSource? source = Interlocked.Exchange(ref _source, null);
        if (source is null)
        {
            return;
        }

        try
        {
            await StopIfActiveAsync(source).ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    async ValueTask StopIfActiveAsync(IMiaCameraFrameSource source)
    {
        if (Interlocked.Exchange(ref _active, 0) != 0)
        {
            await source.StopAsync().ConfigureAwait(false);
        }
    }

    void RegisterDemand()
    {
        Volatile.Write(ref _lastDemandTimestamp, Stopwatch.GetTimestamp());
        _ = Interlocked.Increment(ref _demandVersion);
        ScheduleActivation();
        ScheduleIdleMonitor();
    }

    void ScheduleActivation()
    {
        if (Volatile.Read(ref _active) != 0 ||
            Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _activationError) is not null ||
            Interlocked.CompareExchange(ref _activationScheduled, 1, 0) != 0)
        {
            return;
        }
        _ = EnsureActiveAsync();
    }

    void ScheduleIdleMonitor()
    {
        if (Volatile.Read(ref _disposed) == 0 &&
            Interlocked.CompareExchange(ref _idleMonitorScheduled, 1, 0) == 0)
        {
            _ = DeactivateAfterIdleAsync();
        }
    }

    async Task EnsureActiveAsync()
    {
        try
        {
            _ = await _transitionGate.Reader.ReadAsync(_lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            IMiaCameraFrameSource? source = Volatile.Read(ref _source);
            if (source is null || Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _active) != 0 ||
                Volatile.Read(ref _activationError) is not null)
            {
                return;
            }

            try
            {
                await source.StartAsync(_lifetime.Token).ConfigureAwait(false);
                Volatile.Write(ref _active, 1);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error) when (IsExpectedBackendFailure(error))
            {
                await CleanUpFailedStartAsync(source).ConfigureAwait(false);
                Volatile.Write(ref _activationError, error);
                PublishBackendFailure(error);
            }
        }
        finally
        {
            Volatile.Write(ref _activationScheduled, 0);
            _transitionGate.Writer.TryWrite(0);
        }
    }

    async Task DeactivateAfterIdleAsync()
    {
        bool ownsIdleMonitor = true;
        try
        {
            ownsIdleMonitor = await MonitorUntilIdleAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (IsExpectedBackendFailure(error))
        {
            PublishBackendFailure(error);
        }
        finally
        {
            ReleaseIdleMonitorIfOwned(ownsIdleMonitor);
        }
    }

    async Task<bool> MonitorUntilIdleAsync()
    {
        bool releasedIdleMonitor = false;
        while (Volatile.Read(ref _disposed) == 0 && !releasedIdleMonitor)
        {
            releasedIdleMonitor = await WaitAndDeactivateIfIdleAsync()
                .ConfigureAwait(false);
        }
        return !releasedIdleMonitor;
    }

    async Task<bool> WaitAndDeactivateIfIdleAsync()
    {
        long demandVersion = Volatile.Read(ref _demandVersion);
        await WaitForIdleAsync().ConfigureAwait(false);
        return await DeactivateIfDemandIsCurrentAsync(demandVersion)
            .ConfigureAwait(false);
    }

    async Task WaitForIdleAsync()
    {
        long lastDemand = Volatile.Read(ref _lastDemandTimestamp);
        TimeSpan remaining = _idleTimeout -
            Stopwatch.GetElapsedTime(lastDemand);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _lifetime.Token).ConfigureAwait(false);
        }
    }

    async Task<bool> DeactivateIfDemandIsCurrentAsync(long demandVersion)
    {
        _ = await _transitionGate.Reader.ReadAsync(_lifetime.Token)
            .ConfigureAwait(false);
        bool shouldDeactivate;
        bool restartForNewDemand;
        try
        {
            shouldDeactivate = IsIdleDemandCurrent(demandVersion);
            await DeactivateIfCurrentAsync(shouldDeactivate)
                .ConfigureAwait(false);
            restartForNewDemand = shouldDeactivate &&
                demandVersion != Volatile.Read(ref _demandVersion);
        }
        finally
        {
            _transitionGate.Writer.TryWrite(0);
        }
        RestartAfterNewDemand(restartForNewDemand);
        return shouldDeactivate;
    }

    bool IsIdleDemandCurrent(long demandVersion) =>
        demandVersion == Volatile.Read(ref _demandVersion) &&
        Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDemandTimestamp)) >=
            _idleTimeout;

    async Task DeactivateIfCurrentAsync(bool shouldDeactivate)
    {
        if (shouldDeactivate)
        {
            Volatile.Write(ref _idleMonitorScheduled, 0);
            await StopActiveSourceAsync().ConfigureAwait(false);
        }
    }

    void RestartAfterNewDemand(bool restartForNewDemand)
    {
        if (restartForNewDemand)
        {
            ScheduleActivation();
            ScheduleIdleMonitor();
        }
    }

    void ReleaseIdleMonitorIfOwned(bool ownsIdleMonitor)
    {
        if (ownsIdleMonitor)
        {
            Volatile.Write(ref _idleMonitorScheduled, 0);
        }
    }

    async ValueTask StopActiveSourceAsync()
    {
        IMiaCameraFrameSource? source = Volatile.Read(ref _source);
        if (source is not null && Interlocked.Exchange(ref _active, 0) != 0)
        {
            await source.StopAsync().ConfigureAwait(false);
        }
    }

    static async ValueTask CleanUpFailedStartAsync(
        IMiaCameraFrameSource source)
    {
        try
        {
            await source.StopAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (IsExpectedBackendFailure(error))
        {
            // Preserve the start failure, which explains why capture failed.
        }
    }

    void PublishBackendFailure(Exception error) => BackendFailed?.Invoke(error);

    static bool IsExpectedBackendFailure(Exception error) =>
        error is InvalidOperationException or
            UnauthorizedAccessException or
            TimeoutException or
            NotSupportedException or
            ArgumentException or
            OverflowException or
            ExternalException or
            TypeInitializationException or
            AggregateException or
            IOException or
            OperationCanceledException;

    static Channel<byte> CreateTransitionGate()
    {
        Channel<byte> gate = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        gate.Writer.TryWrite(0);
        return gate;
    }
}
