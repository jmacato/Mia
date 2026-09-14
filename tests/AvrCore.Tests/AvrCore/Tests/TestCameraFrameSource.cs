// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal sealed class TestCameraFrameSource : IMiaCameraFrameSource, IDisposable
{
    MiaCameraFrame? _frame;
    int _missesBeforeFrame;

    public TestCameraFrameSource(MiaCameraFrame frame)
    {
        _frame = frame;
    }

    public string DisplayName => "Test camera";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public MiaCameraFrame? Frame => _frame;

    public int FrameRequestCount { get; private set; }

    public void Publish(MiaCameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
    }

    public void DelayFrameAvailability(int misses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(misses);
        _missesBeforeFrame = misses;
        FrameRequestCount = 0;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync()
    {
        _frame = null;
        return ValueTask.CompletedTask;
    }

    public bool TryGetLatestFrame(out MiaCameraFrame? frame)
    {
        FrameRequestCount++;
        if (_missesBeforeFrame > 0)
        {
            _missesBeforeFrame--;
            frame = null;
            return false;
        }
        frame = _frame;
        return frame is not null;
    }

    public ValueTask DisposeAsync() => StopAsync();

    public void Dispose() => _frame = null;
}
