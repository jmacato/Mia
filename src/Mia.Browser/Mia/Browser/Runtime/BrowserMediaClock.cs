// SPDX-License-Identifier: MIT

namespace Mia.Browser.Runtime;

internal sealed class BrowserMediaClock(uint initialRead, Action<ReadOnlyMemory<byte>> present)
{
    public const int SampleRate = 48_000;
    public const int TargetQueuedSamples = SampleRate / 10;
    const int MaximumFrames = 16;
    readonly Queue<(long Sample, ReadOnlyMemory<byte> Frame)> _frames = new(MaximumFrames);
    uint _lastRead = initialRead;
    long _playedSamples;

    internal int PendingFrames => _frames.Count;

    public void Enqueue(long cycle, ReadOnlyMemory<byte> frame)
    {
        if (_frames.Count == MaximumFrames) _frames.Dequeue();
        _frames.Enqueue((cycle * SampleRate / 13_000_000, frame));
    }

    public void Advance(uint read)
    {
        _playedSamples += unchecked(read - _lastRead);
        _lastRead = read;
        ReadOnlyMemory<byte>? latest = null;
        while (_frames.TryPeek(out var next) && next.Sample <= _playedSamples)
            latest = _frames.Dequeue().Frame;
        if (latest is { } frame) present(frame);
    }

    public int GetDelayMilliseconds(long producedCycle)
    {
        // Include PCM still being rendered, so a stalled audio worker cannot
        // make the core run arbitrarily far ahead of the presentation clock.
        long outstanding = producedCycle * SampleRate / 13_000_000 - _playedSamples;
        return (int)Math.Clamp((outstanding - TargetQueuedSamples) * 1_000 / SampleRate, 0, 6);
    }
}
