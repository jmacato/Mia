// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal readonly ref struct MiaCommuniCamJpegSamplingBlock(
    MiaCameraFrame frame,
    int width,
    int height,
    int blockX,
    int blockY,
    int cropX,
    int cropY,
    int cropWidth,
    int cropHeight,
    double[] luminance,
    double[] blueDifference,
    double[] redDifference)
{
    public MiaCameraFrame Frame { get; } = frame;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int BlockX { get; } = blockX;
    public int BlockY { get; } = blockY;
    public int CropX { get; } = cropX;
    public int CropY { get; } = cropY;
    public int CropWidth { get; } = cropWidth;
    public int CropHeight { get; } = cropHeight;
    public double[] Luminance { get; } = luminance;
    public double[] BlueDifference { get; } = blueDifference;
    public double[] RedDifference { get; } = redDifference;
}
