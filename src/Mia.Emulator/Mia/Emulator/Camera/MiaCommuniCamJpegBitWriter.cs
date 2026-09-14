// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal sealed class MiaCommuniCamJpegBitWriter
{
    readonly List<byte> _output;
    uint _pending;
    int _pendingCount;

    public MiaCommuniCamJpegBitWriter(List<byte> output) =>
        _output = output;

    public void Write(uint value, int count)
    {
        if (count == 0)
        {
            return;
        }
        _pending = (_pending << count) | (value & ((1u << count) - 1));
        _pendingCount += count;
        while (_pendingCount >= 8)
        {
            int shift = _pendingCount - 8;
            WriteByte((byte)(_pending >> shift));
            _pendingCount -= 8;
            _pending &= _pendingCount == 0
                ? 0
                : (1u << _pendingCount) - 1;
        }
    }

    public void Flush()
    {
        if (_pendingCount != 0)
        {
            int padding = 8 - _pendingCount;
            Write((uint)((1 << padding) - 1), padding);
        }
    }

    void WriteByte(byte value)
    {
        _output.Add(value);
        if (value == 0xff)
        {
            _output.Add(0);
        }
    }
}
