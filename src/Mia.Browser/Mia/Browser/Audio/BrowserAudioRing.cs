// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Mia.Browser.Audio;

internal sealed class BrowserAudioRing : IDisposable
{
    // One pinned 32 KiB transport for the page's single audio output. Keeping
    // its storage alive across stops also protects an in-flight worklet read.
    public const int Capacity = 16_384;
    const int HeaderWords = 4;
    static readonly object Gate = new();
    static readonly int[] Storage = new int[HeaderWords + Capacity / 2];
    static readonly GCHandle Pin = GCHandle.Alloc(Storage, GCHandleType.Pinned);
    readonly int _generation;
    readonly int _maximumSamples;

    public BrowserAudioRing(int sampleRate)
    {
        _maximumSamples = Math.Min(Capacity, sampleRate / 5);
        lock (Gate)
        {
            Volatile.Write(ref Storage[2], 0);
            _generation = Interlocked.Increment(ref Storage[3]);
            Interlocked.Exchange(ref Storage[1], Volatile.Read(ref Storage[0]));
            Volatile.Write(ref Storage[2], 1);
        }
    }

    public int Address => (int)Pin.AddrOfPinnedObject();
    public bool IsActive => Volatile.Read(ref Storage[3]) == _generation &&
        Volatile.Read(ref Storage[2]) != 0;

    public static bool TryReadClock(out uint read, out int queued)
    {
        read = unchecked((uint)Volatile.Read(ref Storage[1]));
        uint write = unchecked((uint)Volatile.Read(ref Storage[0]));
        queued = (int)Math.Min(unchecked(write - read), (uint)Capacity);
        return Volatile.Read(ref Storage[2]) != 0;
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (Gate)
        {
            if (Storage[3] != _generation || Storage[2] == 0) return;
            if (samples.Length > _maximumSamples)
                samples = samples[^_maximumSamples..];
            int write = Storage[0];
            while (true)
            {
                int read = Volatile.Read(ref Storage[1]);
                uint queued = unchecked((uint)(write - read));
                if (queued + samples.Length <= _maximumSamples) break;
                // The consumer commits its read with compare/exchange. If an
                // overflow overtakes that read, it discards the affected block.
                int retainedStart = unchecked(write + samples.Length - _maximumSamples);
                if (Interlocked.CompareExchange(ref Storage[1], retainedStart, read) == read)
                    break;
            }
            Span<short> pcm = MemoryMarshal.Cast<int, short>(Storage.AsSpan(HeaderWords));
            int offset = write & (Capacity - 1);
            int first = Math.Min(samples.Length, Capacity - offset);
            samples[..first].CopyTo(pcm[offset..]);
            samples[first..].CopyTo(pcm);
            Volatile.Write(ref Storage[0], unchecked(write + samples.Length));
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (Storage[3] != _generation) return;
            Volatile.Write(ref Storage[2], 0);
            Interlocked.Increment(ref Storage[3]);
            Interlocked.Exchange(ref Storage[1], Volatile.Read(ref Storage[0]));
        }
    }
}
