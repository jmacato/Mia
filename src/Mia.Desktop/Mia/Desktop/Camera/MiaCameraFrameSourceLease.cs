// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Camera;

/// <summary>
/// Owns one demand-activated host-camera backend. Attaching the accessory does
/// not start capture; the first frame request does.
/// </summary>
internal sealed class MiaCameraFrameSourceLease : IAsyncDisposable
{
    MiaDemandActivatedCameraFrameSource? _source;

    MiaCameraFrameSourceLease()
    {
    }

    public IMiaCameraFrameSource Source => Volatile.Read(ref _source) ??
        throw new ObjectDisposedException(nameof(MiaCameraFrameSourceLease));

    public event Action<Exception>? BackendFailed
    {
        add
        {
            if (Volatile.Read(ref _source) is { } source)
            {
                source.BackendFailed += value;
            }
        }
        remove
        {
            if (Volatile.Read(ref _source) is { } source)
            {
                source.BackendFailed -= value;
            }
        }
    }

    public static async ValueTask<MiaCameraFrameSourceLease> CreateAsync(
        IMiaCameraFrameSourceFactory factory,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        IMiaCameraFrameSource? source = factory.Create() ??
            throw new InvalidOperationException(
                "The host camera backend did not create a frame source.");
        var lease = new MiaCameraFrameSourceLease();
        try
        {
            lease._source = new MiaDemandActivatedCameraFrameSource(
                source,
                idleTimeout);
            source = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!lease._source.IsAvailable)
            {
                throw new InvalidOperationException(
                    lease._source.UnavailableReason ??
                    "The host camera is unavailable.");
            }
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        MiaDemandActivatedCameraFrameSource? source = _source;
        _source = null;
        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
