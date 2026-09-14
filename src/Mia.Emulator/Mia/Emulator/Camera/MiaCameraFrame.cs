// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// One immutable host-camera snapshot. The producer transfers ownership of
/// the backing memory and must not modify it after publishing the frame.
/// </summary>
internal sealed class MiaCameraFrame
{
    MiaCameraFrame(
        int width,
        int height,
        int stride,
        MiaCameraPixelFormat pixelFormat,
        ReadOnlyMemory<byte> pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        Pixels = pixels;
    }

    public static MiaCameraFrame Create(
        int width,
        int height,
        int stride,
        MiaCameraPixelFormat pixelFormat,
        ReadOnlyMemory<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int minimumStride = pixelFormat switch
        {
            MiaCameraPixelFormat.Rgb332 => width,
            MiaCameraPixelFormat.Bgra32 => checked(width * 4),
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };
        if (stride < minimumStride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stride),
                $"Stride must be at least {minimumStride} bytes.");
        }
        if (pixels.Length < checked(stride * height))
        {
            throw new ArgumentException(
                "The pixel buffer is shorter than the declared frame layout.",
                nameof(pixels));
        }

        return new(width, height, stride, pixelFormat, pixels);
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public MiaCameraPixelFormat PixelFormat { get; }

    public ReadOnlyMemory<byte> Pixels { get; }
}
