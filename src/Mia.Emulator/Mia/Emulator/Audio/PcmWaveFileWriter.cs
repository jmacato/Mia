// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Mia.Emulator.Audio;

/// <summary>
/// Streaming mono signed-16 WAV writer used for deterministic firmware-audio
/// captures. The final RIFF sizes are patched when the writer is disposed.
/// </summary>
internal sealed class PcmWaveFileWriter : IDisposable
{
    const int HeaderLength = 44;

    readonly FileStream _stream;
    readonly byte[] _byteBuffer = new byte[16 * 1024];
    long _sampleCount;
    bool _disposed;

    public PcmWaveFileWriter(string path, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        _stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read);
        _stream.Write(new byte[HeaderLength]);
    }

    public int SampleRate { get; }

    public long SampleCount => _sampleCount;

    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sampleCount > (uint.MaxValue - HeaderLength) / sizeof(short) - samples.Length)
        {
            throw new InvalidOperationException("The PCM capture exceeds the WAV size limit.");
        }

        while (!samples.IsEmpty)
        {
            var count = Math.Min(samples.Length, _byteBuffer.Length / sizeof(short));
            var bytes = _byteBuffer.AsSpan(0, count * sizeof(short));
            for (var index = 0; index < count; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    bytes.Slice(index * sizeof(short), sizeof(short)),
                    samples[index]);
            }
            _stream.Write(bytes);
            _sampleCount += count;
            samples = samples[count..];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var dataLength = checked((uint)(_sampleCount * sizeof(short)));
        Span<byte> header = stackalloc byte[HeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], dataLength + 36);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..],
            checked((uint)(SampleRate * sizeof(short))));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataLength);
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Dispose();
    }
}
