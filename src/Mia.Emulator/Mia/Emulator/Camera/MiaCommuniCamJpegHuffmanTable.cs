// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal sealed class MiaCommuniCamJpegHuffmanTable
{
    readonly ushort[] _codes = new ushort[256];
    readonly byte[] _lengths = new byte[256];

    public MiaCommuniCamJpegHuffmanTable(
        ReadOnlySpan<byte> counts,
        ReadOnlySpan<byte> values)
    {
        var code = 0;
        var valueIndex = 0;
        for (var length = 1; length <= counts.Length; length++)
        {
            for (var index = 0; index < counts[length - 1]; index++)
            {
                byte value = values[valueIndex++];
                _codes[value] = checked((ushort)code++);
                _lengths[value] = checked((byte)length);
            }
            code <<= 1;
        }
        if (valueIndex != values.Length)
        {
            throw new ArgumentException(
                "Huffman counts do not match the value table.",
                nameof(values));
        }
    }

    public void Write(byte value, MiaCommuniCamJpegBitWriter writer)
    {
        int length = _lengths[value];
        if (length == 0)
        {
            throw new InvalidOperationException(
                "The JPEG coefficient has no Huffman code.");
        }
        writer.Write(_codes[value], length);
    }
}
