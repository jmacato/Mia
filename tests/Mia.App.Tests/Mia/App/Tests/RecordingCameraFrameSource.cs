// SPDX-License-Identifier: MIT

using Mia.Emulator.Camera;

namespace Mia.App.Tests;

internal sealed class RecordingCameraFrameSource : IMiaCameraFrameSource
{
    bool _disposed;

    public string DisplayName => "Test camera";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public Exception? StartError { get; init; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        return StartError is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(StartError);
    }

    public ValueTask StopAsync()
    {
        StopCount++;
        return ValueTask.CompletedTask;
    }

    public bool TryGetLatestFrame(out MiaCameraFrame? frame)
    {
        frame = null;
        return false;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            DisposeCount++;
        }
        return ValueTask.CompletedTask;
    }
}
