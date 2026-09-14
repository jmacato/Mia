// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal readonly ref struct MiaCommuniCamJpegEncodingBlock(
    double[] samples,
    ReadOnlySpan<byte> quantization,
    int[] coefficients,
    MiaCommuniCamJpegHuffmanTable dcTable,
    MiaCommuniCamJpegHuffmanTable acTable,
    MiaCommuniCamJpegBitWriter bits)
{
    public double[] Samples { get; } = samples;
    public ReadOnlySpan<byte> Quantization { get; } = quantization;
    public int[] Coefficients { get; } = coefficients;
    public MiaCommuniCamJpegHuffmanTable DcTable { get; } = dcTable;
    public MiaCommuniCamJpegHuffmanTable AcTable { get; } = acTable;
    public MiaCommuniCamJpegBitWriter Bits { get; } = bits;
}
