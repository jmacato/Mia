// SPDX-License-Identifier: MIT

using OpenTK.Audio.OpenAL;

namespace Mia.Desktop.Audio;

/// <summary>
/// Small bounded OpenAL stream for the mono PCM published by the desktop
/// emulator-session adapter.
/// </summary>
internal sealed class OpenAlPcmSink : IMainViewAudioOutput
{
    const int BufferCount = 8;
    const int ChunkMilliseconds = 20;

    readonly ALDevice _device;
    readonly ALContext _context;
    readonly int _source;
    readonly int[] _buffers;
    readonly Queue<int> _availableBuffers;
    readonly short[] _chunk;
    int _chunkLength;
    bool _disposed;

    public OpenAlPcmSink(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        _chunk = new short[sampleRate * ChunkMilliseconds / 1_000];

        _device = ALC.OpenDevice(null);
        if (_device == ALDevice.Null)
        {
            throw new InvalidOperationException("OpenAL could not open the default output device.");
        }

        _context = ALC.CreateContext(_device, (int[]?)null);
        if (_context == ALContext.Null || !ALC.MakeContextCurrent(_context))
        {
            ALC.CloseDevice(_device);
            throw new InvalidOperationException("OpenAL could not create an output context.");
        }

        _source = AL.GenSource();
        _buffers = AL.GenBuffers(BufferCount);
        _availableBuffers = new Queue<int>(_buffers);
        ThrowIfAlError("initialize audio output");
    }

    public int SampleRate { get; }

    public long DroppedSampleCount { get; private set; }

    public void PushSamples(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!samples.IsEmpty)
        {
            var copied = Math.Min(samples.Length, _chunk.Length - _chunkLength);
            samples[..copied].CopyTo(_chunk.AsSpan(_chunkLength));
            samples = samples[copied..];
            _chunkLength += copied;
            if (_chunkLength == _chunk.Length)
            {
                QueueChunk();
                _chunkLength = 0;
            }
        }
    }

    void QueueChunk()
    {
        ReclaimProcessedBuffers();
        if (!_availableBuffers.TryDequeue(out var buffer))
        {
            DroppedSampleCount += _chunk.Length;
            return;
        }

        AL.BufferData(
            buffer,
            ALFormat.Mono16,
            _chunk.AsSpan(),
            SampleRate);
        AL.SourceQueueBuffer(_source, buffer);
        ThrowIfAlError("queue audio samples");

        var state = (ALSourceState)AL.GetSource(_source, ALGetSourcei.SourceState);
        if (state != ALSourceState.Playing)
        {
            AL.SourcePlay(_source);
        }
    }

    void ReclaimProcessedBuffers()
    {
        var processed = AL.GetSource(_source, ALGetSourcei.BuffersProcessed);
        while (processed-- > 0)
        {
            _availableBuffers.Enqueue(AL.SourceUnqueueBuffer(_source));
        }
    }

    static void ThrowIfAlError(string operation)
    {
        var error = AL.GetError();
        if (error != ALError.NoError)
        {
            throw new InvalidOperationException(
                $"OpenAL could not {operation}: {error}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DisposeContext();
        CloseDevice();
    }

    void DisposeContext()
    {
        if (_context != ALContext.Null)
        {
            ALC.MakeContextCurrent(_context);
            AL.SourceStop(_source);
            UnqueueAllBuffers();
            AL.DeleteSource(_source);
            AL.DeleteBuffers(_buffers);
            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(_context);
        }
    }

    void UnqueueAllBuffers()
    {
        var queued = AL.GetSource(_source, ALGetSourcei.BuffersQueued);
        while (queued-- > 0)
        {
            _ = AL.SourceUnqueueBuffer(_source);
        }
    }

    void CloseDevice()
    {
        if (_device != ALDevice.Null)
        {
            ALC.CloseDevice(_device);
        }
    }
}
