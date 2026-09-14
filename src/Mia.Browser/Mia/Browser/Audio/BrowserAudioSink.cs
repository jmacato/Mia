// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Avalonia.Threading;

namespace Mia.Browser.Audio;

/// <summary>
/// Publishes PCM directly to the AudioWorklet through shared memory. The
/// bounded UI queue is used only by runtimes without shared WASM memory.
/// </summary>
internal sealed class BrowserAudioSink : IDisposable
{
    const int MaximumBufferedMilliseconds = 200;

    readonly ConcurrentQueue<short[]> _pending = new();
    readonly int _maximumBufferedSamples;
    readonly BrowserAudioRing? _ring;
    int _pendingSamples;
    int _drainQueuedFlag;
    int _disposedFlag;
    int _interopFailedFlag;

    internal BrowserAudioSink(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        SampleRate = sampleRate;
        _maximumBufferedSamples = Math.Max(
            1,
            sampleRate * MaximumBufferedMilliseconds / 1_000);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "The browser audio sink must be created on the UI thread.");
        }
#if BROWSER_THREADS
        var ring = new BrowserAudioRing(sampleRate);
        if (BrowserAudioInterop.Start(sampleRate, ring.Address, BrowserAudioRing.Capacity))
            _ring = ring;
        else
            ring.Dispose();
#else
        BrowserAudioInterop.Start(sampleRate, 0, 0);
#endif
    }

    public int SampleRate { get; }

    /// <summary>
    /// Queues signed 16-bit mono PCM. Old audio is discarded if the UI or
    /// AudioWorklet falls behind, keeping audible output close to guest time.
    /// This method is safe to call from an emulator worker thread.
    /// </summary>
    public void PushSamples(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty || IsUnavailable)
        {
            return;
        }
        if (_ring is { IsActive: true })
        {
            _ring.Write(samples);
            return;
        }

        var retained = samples.Length <= _maximumBufferedSamples
            ? samples.ToArray()
            : samples[^_maximumBufferedSamples..].ToArray();

        _pending.Enqueue(retained);
        Interlocked.Add(ref _pendingSamples, retained.Length);

        if (IsUnavailable)
        {
            // Dispose or an interop failure raced ahead of the enqueue above;
            // undo it so the sink stays fully emptied.
            DrainAndClear();
        }
        else
        {
            DiscardOverflow();
            ScheduleDrain();
        }
    }

    bool IsUnavailable => Volatile.Read(ref _disposedFlag) != 0 ||
        Volatile.Read(ref _interopFailedFlag) != 0;

    void DiscardOverflow()
    {
        while (Volatile.Read(ref _pendingSamples) > _maximumBufferedSamples &&
            _pending.TryDequeue(out var stale))
        {
            Interlocked.Add(ref _pendingSamples, -stale.Length);
        }
    }

    void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainQueuedFlag, 1, 0) != 0)
        {
            return;
        }

        BrowserDispatcher.Post(Drain, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposedFlag, 1, 0) != 0)
        {
            return;
        }
        DrainAndClear();
        _ring?.Dispose();

        if (Dispatcher.UIThread.CheckAccess())
        {
            StopInterop();
        }
        else
        {
            BrowserDispatcher.Post(StopInterop, DispatcherPriority.Send);
        }
    }

    void Drain()
    {
        while (!IsUnavailable && TryTakeNext(out var samples))
        {
            Interlocked.Add(ref _pendingSamples, -samples.Length);
            SubmitSamples(samples);
        }
        ResetUnavailableDrain();
    }

    bool TryTakeNext([NotNullWhen(true)] out short[]? samples)
    {
        while (!_pending.TryDequeue(out samples))
        {
            Volatile.Write(ref _drainQueuedFlag, 0);
            // A producer can observe the flag before the reset above. Reclaim
            // it when that race left an item with no drain scheduled.
            if (_pending.IsEmpty ||
                Interlocked.CompareExchange(ref _drainQueuedFlag, 1, 0) != 0)
            {
                return false;
            }
        }
        return true;
    }

    void ResetUnavailableDrain()
    {
        if (IsUnavailable)
        {
            Volatile.Write(ref _drainQueuedFlag, 0);
        }
    }

    void SubmitSamples(short[] samples)
    {
        try
        {
            BrowserAudioInterop.PushSamples(
                MemoryMarshal.AsBytes(samples.AsSpan()));
        }
        catch (Exception error) when (error is JSException or
            InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Mia browser audio output failed: {error.Message}");
            Volatile.Write(ref _interopFailedFlag, 1);
            DrainAndClear();
        }
    }

    void DrainAndClear()
    {
        while (_pending.TryDequeue(out var discarded))
        {
            Interlocked.Add(ref _pendingSamples, -discarded.Length);
        }
    }

    void StopInterop()
    {
        try
        {
            BrowserAudioInterop.Stop();
        }
        catch (Exception error) when (error is JSException or
            InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Mia browser audio shutdown failed: {error.Message}");
        }
    }
}
