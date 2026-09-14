// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// Encodes the MCA-25's uncompressed eight-bit EBMP monitoring and thumbnail
/// objects. Resampling is accessory-owned; the handset receives the finished
/// object.
/// </summary>
internal static class MiaCommuniCamEbmpEncoder
{
    public const int HeaderLength = 14;
    public const int TerminatorLength = 1;

    public static byte[] Encode(MiaCameraFrame? frame, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);

        // Captured MCA-25 objects include one zero byte after the raster. Its
        // OBEX Object Length is consequently 4,815 for an 80x60 frame.
        var result = new byte[
            checked(HeaderLength + width * height + TerminatorLength)];
        Span<byte> header = result.AsSpan(0, HeaderLength);
        "EBMP_1.0"u8.CopyTo(header);
        header[8] = 0x05;
        header[9] = checked((byte)(width >> 8));
        header[10] = checked((byte)width);
        header[11] = checked((byte)(height >> 8));
        header[12] = checked((byte)height);
        header[13] = 8;
        if (frame is null)
        {
            return result;
        }

        int cropWidth = Math.Min(frame.Width, frame.Height * 4 / 3);
        int cropHeight = Math.Min(frame.Height, frame.Width * 3 / 4);
        int cropX = (frame.Width - cropWidth) / 2;
        int cropY = (frame.Height - cropHeight) / 2;
        ReadOnlySpan<byte> source = frame.Pixels.Span;
        Span<byte> destination = result.AsSpan(HeaderLength, width * height);
        for (var y = 0; y < height; y++)
        {
            int sourceY = cropY + y * cropHeight / height;
            for (var x = 0; x < width; x++)
            {
                int sourceX = cropX + x * cropWidth / width;
                destination[y * width + x] =
                    ReadRgb332(frame, source, sourceX, sourceY);
            }
        }
        return result;
    }

    static byte ReadRgb332(
        MiaCameraFrame frame,
        ReadOnlySpan<byte> source,
        int x,
        int y)
    {
        int offset = y * frame.Stride;
        if (frame.PixelFormat == MiaCameraPixelFormat.Rgb332)
        {
            return source[offset + x];
        }

        offset += x * 4;
        byte blue = source[offset];
        byte green = source[offset + 1];
        byte red = source[offset + 2];
        return (byte)((red & 0xe0) | ((green >> 3) & 0x1c) | (blue >> 6));
    }
}
