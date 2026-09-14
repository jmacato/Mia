// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Mia.Emulator.Camera;

/// <summary>
/// One image retained in the accessory's volatile memory. All derived forms
/// use the same immutable source snapshot.
/// </summary>
internal sealed class MiaCommuniCamStoredImage
{
    readonly MiaCameraFrame _frame;
    byte[]? _jpeg;
    byte[]? _thumbnail;

    public MiaCommuniCamStoredImage(
        int numericHandle,
        MiaCameraFrame frame,
        int width,
        int height,
        DateTimeOffset capturedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numericHandle);
        ArgumentNullException.ThrowIfNull(frame);
        if (!MiaCommuniCamImageProfile.IsSupportedNativeSize(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The MCA-25 does not support the requested native size.");
        }

        Handle = numericHandle.ToString("D7", CultureInfo.InvariantCulture);
        _frame = frame;
        Width = width;
        Height = height;
        Created = capturedAt.UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture);
    }

    public string Handle { get; }

    public string Created { get; }

    public int Width { get; }

    public int Height { get; }

    public byte[] Jpeg =>
        _jpeg ??= MiaCommuniCamJpegEncoder.Encode(_frame, Width, Height);

    public byte[] Thumbnail =>
        _thumbnail ??= MiaCommuniCamEbmpEncoder.Encode(
            _frame,
            MiaCommuniCamImageProfile.ThumbnailWidth,
            MiaCommuniCamImageProfile.ThumbnailHeight);

    public byte[] GetJpeg(int width, int height) =>
        width == Width && height == Height
            ? Jpeg
            : MiaCommuniCamJpegEncoder.Encode(_frame, width, height);
}
