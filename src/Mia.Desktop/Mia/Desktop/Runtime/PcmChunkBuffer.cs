// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Runtime;

internal sealed class PcmChunkBuffer
{
    readonly short[] _chunk;
    readonly Action<ReadOnlyMemory<short>> _publish;
    int _length;

    public PcmChunkBuffer(
        int chunkLength,
        Action<ReadOnlyMemory<short>> publish)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkLength);
        _chunk = new short[chunkLength];
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        while (!samples.IsEmpty)
        {
            int copied = Math.Min(samples.Length, _chunk.Length - _length);
            samples[..copied].CopyTo(_chunk.AsSpan(_length));
            samples = samples[copied..];
            _length += copied;
            PublishCompleteChunk();
        }
    }

    void PublishCompleteChunk()
    {
        if (_length != _chunk.Length)
        {
            return;
        }

        _publish(_chunk.ToArray());
        _length = 0;
    }
}
