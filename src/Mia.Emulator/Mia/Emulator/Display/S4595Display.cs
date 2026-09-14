// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Display;

/// <summary>
/// Transaction-level model of the S-4595 LCD controller used by the common
/// Mia display module. Addresses are the 8-bit I2C wire values used by the
/// firmware's display description, rather than seven-bit bus addresses.
/// </summary>
internal sealed class S4595Display
{
    public const byte WriteAddress = 0x72;
    public const int Width = 101;
    public const int Height = 80;
    public const byte DefaultColumnStart = 0x1b;
    public const byte DefaultColumnEnd = 0x7f;
    public const byte DefaultRowStart = 0x00;
    public const byte DefaultRowEnd = 0x4f;
    public const byte ScanRowCommand = 0x8c;
    public const byte ModeRegister = 0x01;
    public const byte PartialDisplayMask = 0x08;
    public const byte PartialRowCountRegister = 0x11;
    public const byte PartialRowStartRegister = 0x16;

    readonly byte[] _registers = new byte[256];
    readonly byte[] _framebuffer = new byte[Width * Height];
    readonly byte[] _visibleFrame = new byte[Width * Height];

    public S4595Display() => Reset();

    public ReadOnlySpan<byte> Framebuffer
    {
        get
        {
            if ((_registers[ModeRegister] & PartialDisplayMask) == 0)
            {
                return _framebuffer;
            }

            // Low-power scanout only drives the selected row band. RAM outside
            // it is retained, but must not show through behind the idle clock.
            _visibleFrame.AsSpan().Clear();
            int start = Math.Min((int)_registers[PartialRowStartRegister], Height);
            int count = Math.Min(_registers[PartialRowCountRegister] + 1, Height - start);
            _framebuffer.AsSpan(start * Width, count * Width)
                .CopyTo(_visibleFrame.AsSpan(start * Width));
            return _visibleFrame;
        }
    }

    public long TransactionCount { get; private set; }

    public long PixelWriteCount { get; private set; }

    public byte CursorColumn => _registers[0x04];

    public byte CursorRow => _registers[0x05];

    public byte ReadRegister(byte register) => _registers[register];

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_framebuffer);
        _registers[0x04] = DefaultColumnStart;
        _registers[0x05] = DefaultRowStart;
        _registers[0x07] = DefaultColumnStart;
        _registers[0x08] = DefaultColumnEnd;
        _registers[0x09] = DefaultRowStart;
        _registers[0x0a] = DefaultRowEnd;
        TransactionCount = 0;
        PixelWriteCount = 0;
    }

    /// <summary>
    /// Applies one complete controller write. Returns false when the address is
    /// not acknowledged by this panel; an acknowledged empty write is valid.
    /// </summary>
    public bool WriteTransaction(byte address, ReadOnlySpan<byte> payload)
    {
        if (address != WriteAddress)
        {
            return false;
        }

        TransactionCount++;
        if (payload.IsEmpty)
        {
            return true;
        }

        if (payload[0] == ScanRowCommand)
        {
            WritePixels(payload[1..]);
            return true;
        }

        WriteRegisterPairs(payload);
        return true;
    }

    void WritePixels(ReadOnlySpan<byte> pixels)
    {
        foreach (byte pixel in pixels)
        {
            WritePixel(pixel);
        }
    }

    void WriteRegisterPairs(ReadOnlySpan<byte> payload)
    {
        // The S-4595 command scripts are sequences of register/value pairs.
        // Preserve a trailing register byte if a diagnostic probe ends early,
        // but do not invent its value.
        for (var index = 0; index + 1 < payload.Length; index += 2)
        {
            _registers[payload[index]] = payload[index + 1];
        }
    }

    public byte GetPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return _framebuffer[y * Width + x];
    }

    /// <summary>
    /// Expands the firmware-declared 8/3/3/2 color layout to 24-bit RGB.
    /// </summary>
    public static (byte Red, byte Green, byte Blue) ExpandRgb332(byte pixel)
    {
        var red = (byte)((pixel >> 5) * 255 / 7);
        var green = (byte)(((pixel >> 2) & 7) * 255 / 7);
        var blue = (byte)((pixel & 3) * 255 / 3);
        return (red, green, blue);
    }

    void WritePixel(byte pixel)
    {
        var columnStart = _registers[0x07];
        var columnEnd = _registers[0x08];
        var rowStart = _registers[0x09];
        var rowEnd = _registers[0x0a];
        var column = CursorColumn;
        var row = CursorRow;

        var x = column - DefaultColumnStart;
        var y = row - DefaultRowStart;
        if (column >= columnStart && column <= columnEnd &&
            row >= rowStart && row <= rowEnd &&
            x >= 0 && x < Width && y >= 0 && y < Height)
        {
            _framebuffer[y * Width + x] = pixel;
            PixelWriteCount++;
        }

        if (column < columnEnd)
        {
            _registers[0x04] = (byte)(column + 1);
            return;
        }

        _registers[0x04] = columnStart;
        _registers[0x05] = row < rowEnd ? (byte)(row + 1) : rowStart;
    }
}
