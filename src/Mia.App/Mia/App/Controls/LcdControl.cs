// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Mia.App.Controls;

public sealed class LcdControl : Image
{
    public const int PixelWidth = 101;
    public const int PixelHeight = 80;
    const int BytesPerPixel = 4;
    const int BitmapBufferCount = 3;
    const double BacklightOffOpacity = 0.18;
    static readonly int[] Palette = CreatePalette();

    readonly WriteableBitmap[] _bitmaps;
    readonly int[] _pixels = new int[PixelWidth * PixelHeight];
    int _nextBitmapIndex;
    bool _backlightOn;

    public LcdControl()
    {
        const double windowPadding = 15;
        Rect screenBounds = MiaPhoneGeometry.CreateScreenWindow().Bounds;
        Width = Math.Max(0, screenBounds.Width - windowPadding * 2);
        Height = Math.Max(0, screenBounds.Height - windowPadding * 2);
        Canvas.SetLeft(this, screenBounds.X + windowPadding);
        Canvas.SetTop(this, screenBounds.Y + windowPadding);
        _bitmaps = Enumerable.Range(0, BitmapBufferCount)
            .Select(_ => new WriteableBitmap(
                new PixelSize(PixelWidth, PixelHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque))
            .ToArray();
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Opacity = BacklightOffOpacity;
        UpdateFrame(new byte[PixelWidth * PixelHeight]);
    }

    internal bool BacklightOn => _backlightOn;

    internal void SetBacklight(bool value)
    {
        if (_backlightOn == value)
        {
            return;
        }

        _backlightOn = value;
        Opacity = ResolveBacklightOpacity(value);
    }

    internal static double ResolveBacklightOpacity(bool value) =>
        value ? 1 : BacklightOffOpacity;

    public void UpdateFrame(ReadOnlySpan<byte> rgb332)
    {
        if (rgb332.Length != PixelWidth * PixelHeight)
        {
            throw new ArgumentException("An S-4595 frame must contain exactly 8,080 pixels.", nameof(rgb332));
        }

        var destination = 0;
        foreach (var pixel in rgb332)
        {
            _pixels[destination++] = Palette[pixel];
        }

        var bitmap = _bitmaps[_nextBitmapIndex];
        _nextBitmapIndex = (_nextBitmapIndex + 1) % _bitmaps.Length;
        using (var framebuffer = bitmap.Lock())
        {
            var sourceRowBytes = PixelWidth * BytesPerPixel;
            if (framebuffer.RowBytes == sourceRowBytes)
            {
                Marshal.Copy(_pixels, 0, framebuffer.Address, _pixels.Length);
            }
            else
            {
                for (var y = 0; y < PixelHeight; y++)
                {
                    Marshal.Copy(
                        _pixels,
                        y * PixelWidth,
                        IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                        PixelWidth);
                }
            }
        }
        Source = bitmap;
    }

    static int[] CreatePalette()
    {
        var palette = new int[256];
        for (var pixel = 0; pixel < palette.Length; pixel++)
        {
            byte blue = (byte)((pixel & 3) * 255 / 3);
            byte green = (byte)(((pixel >> 2) & 7) * 255 / 7);
            byte red = (byte)((pixel >> 5) * 255 / 7);
            palette[pixel] = BitConverter.IsLittleEndian
                ? blue | (green << 8) | (red << 16) | unchecked((int)0xff000000)
                : (blue << 24) | (green << 16) | (red << 8) | 0xff;
        }
        return palette;
    }
}
