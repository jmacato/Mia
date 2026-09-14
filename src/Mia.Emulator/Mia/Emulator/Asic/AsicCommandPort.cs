// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Graphics-engine command FIFO used by DisplayGraphics_Process and the
/// 0x0efxxx firmware helpers. Firmware polls status bit 4 before commands.
/// The implemented packet shapes and their external-RAM effects are recovered
/// from those producers; unfamiliar formats fail closed. Command 0x05 reads
/// one RGB332 pixel from the selected destination surface through 0x0172.
/// </summary>
internal sealed class AsicCommandPort
{
    public const int StatusAddress = 0x0170;
    public const int DataAddress = 0x0171;
    public const int ResponseAddress = 0x0172;
    public const byte Ready = 0x10;
    public const int GraphicsRamBase = 0x100000;

    // The live 8-bit surfaces select format 3 and use these fixed command
    // fields. Other formats remain unsupported until their packets execute.
    const byte Rgb332Format = 0x03;
    const byte SurfaceMemoryType = 0x02;
    const byte SurfaceAddressMode = 0x09;
    const byte SurfaceRectangleMode = 0x06;
    const byte BlitterMode = 0x20;
    const int PatternWidth = 8;
    const int PatternHeight = 8;

    readonly Cpu _cpu;
    readonly List<byte> _packet = [];
    int _packetLength;
    AsicCommandPortSurface? _destination;
    AsicCommandPortSourceSurface? _source;
    int? _patternAddress;
    AsicCommandPortMonochromeBitmap? _monochromeBitmap;
    byte _foreground;
    byte _response;

    public long WrittenByteCount { get; private set; }

    public long CompletedPacketCount { get; private set; }

    public long RejectedPacketCount { get; private set; }

    public long RasterOperationCount { get; private set; }

    public long PixelWriteCount { get; private set; }

    public event Action<byte>? ByteWritten;

    public AsicCommandPort(Cpu cpu)
    {
        _cpu = cpu;
        cpu.ReadHooks[StatusAddress] = address => (byte)(cpu.Data[address] | Ready);
        cpu.ReadHooks[ResponseAddress] = _ => _response;
        cpu.WriteHooks[DataAddress] = (value, _, _, _) =>
        {
            WrittenByteCount++;
            ByteWritten?.Invoke(value);
            ReceiveByte(value);
            return false;
        };
    }

    void ReceiveByte(byte value)
    {
        if (_packet.Count == 0 && !BeginPacket(value))
        {
            return;
        }

        _packet.Add(value);
        if (_packet.Count != _packetLength)
        {
            return;
        }

        ExecutePacket(CollectionsMarshal.AsSpan(_packet));
        CompletedPacketCount++;
        _packet.Clear();
        _packetLength = 0;
    }

    bool BeginPacket(byte opcode)
    {
        _packetLength = PacketLength(opcode);
        if (_packetLength != 0)
        {
            return true;
        }

        RejectedPacketCount++;
        return false;
    }

    void ExecutePacket(ReadOnlySpan<byte> packet)
    {
        switch (packet[0])
        {
            case 0x05:
                ReadPenPixel(packet);
                break;
            case 0x06:
                UpdateDestinationRectangle(packet);
                break;
            case 0x0a:
                SelectSource(packet);
                break;
            case 0x0b:
                _foreground = packet[1];
                break;
            case 0x10:
                SelectDestination(packet);
                break;
            case 0x12:
                SelectPattern(packet);
                break;
            case 0x11:
                ExecuteBlt(packet);
                break;
            case 0x0f:
                SelectMonochromeBitmap(packet);
                break;
            case 0x4c:
                ExecuteCopyPenPixel(packet);
                break;
            case 0x80:
                ExecuteMonochromeBlt(packet);
                break;
            case 0x0c:
            case 0x0d:
            case 0x0e:
            case 0x15:
                // Proven setup commands have no CPU-memory-visible result.
                break;
        }
    }

    void ReadPenPixel(ReadOnlySpan<byte> packet)
    {
        if (_destination is not { } destination)
        {
            _response = 0;
            RejectedPacketCount++;
            return;
        }

        var y = packet[1];
        var x = packet[2];
        if (x < destination.Left || x >= destination.Right ||
            y < destination.Top || y >= destination.Bottom)
        {
            _response = 0;
            return;
        }

        _response = _cpu.Data[
            destination.Address + y * destination.Width + x];
    }

    void UpdateDestinationRectangle(ReadOnlySpan<byte> packet)
    {
        if (_destination is not { } destination)
        {
            RejectedPacketCount++;
            return;
        }

        var left = packet[1];
        var right = packet[2];
        var top = packet[3];
        var bottom = packet[4];
        if (left >= right || top >= bottom || right > destination.Width ||
            !ContainsSurface(destination.Address, destination.Width, right, bottom))
        {
            _destination = null;
            RejectedPacketCount++;
            return;
        }

        _destination = destination with
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };
    }

    void SelectDestination(ReadOnlySpan<byte> packet)
    {
        if (packet[1] != Rgb332Format ||
            packet[2] != SurfaceMemoryType ||
            packet[3] != SurfaceAddressMode ||
            packet[7] != SurfaceRectangleMode)
        {
            _destination = null;
            RejectedPacketCount++;
            return;
        }

        var address = GraphicsRamBase | (packet[4] << 8) | packet[5];
        var width = packet[6];
        var left = packet[8];
        var right = packet[9];
        var top = packet[10];
        var bottom = packet[11];
        if (width == 0 || left >= right || top >= bottom || right > width ||
            !ContainsSurface(address, width, right, bottom))
        {
            _destination = null;
            RejectedPacketCount++;
            return;
        }

        _destination = new AsicCommandPortSurface(address, width, left, top, right, bottom);
    }

    void SelectSource(ReadOnlySpan<byte> packet)
    {
        var address = GraphicsRamBase | (packet[1] << 8) | packet[2];
        var stride = packet[3];
        if (stride == 0 || address < GraphicsRamBase || address >= _cpu.Data.Length)
        {
            _source = null;
            RejectedPacketCount++;
            return;
        }
        _source = new AsicCommandPortSourceSurface(address, stride);
    }

    void SelectPattern(ReadOnlySpan<byte> packet)
    {
        var address = GraphicsRamBase | (packet[1] << 8) | packet[2];
        if (address < 0 || address > _cpu.Data.Length - PatternWidth * PatternHeight)
        {
            _patternAddress = null;
            RejectedPacketCount++;
            return;
        }
        _patternAddress = address;
    }

    void SelectMonochromeBitmap(ReadOnlySpan<byte> packet)
    {
        var address = GraphicsRamBase | (packet[1] << 8) | packet[2];
        var width = packet[3];
        var height = packet[4];
        var pageCount = (height + 7) / 8;
        var byteCount = width * pageCount;
        if (width == 0 || height == 0 || address < GraphicsRamBase ||
            address + byteCount > _cpu.Data.Length)
        {
            _monochromeBitmap = null;
            RejectedPacketCount++;
            return;
        }

        _monochromeBitmap = new AsicCommandPortMonochromeBitmap(address, width, height);
    }

    void ExecuteCopyPenPixel(ReadOnlySpan<byte> packet)
    {
        if (_destination is not { } destination)
        {
            RejectedPacketCount++;
            return;
        }

        var y = packet[1];
        var x = packet[2];
        if (x < destination.Left || x >= destination.Right ||
            y < destination.Top || y >= destination.Bottom)
        {
            return;
        }

        _cpu.Data[destination.Address + y * destination.Width + x] = _foreground;
        RasterOperationCount++;
        PixelWriteCount++;
    }

    void ExecuteMonochromeBlt(ReadOnlySpan<byte> packet)
    {
        if (_destination is not { } destination ||
            _monochromeBitmap is not { } bitmap)
        {
            RejectedPacketCount++;
            return;
        }

        var operation = new AsicCommandPortMonochromeOperation(
            bitmap,
            destination,
            packet[1],
            packet[2]);
        for (var sourceY = 0; sourceY < bitmap.Height; sourceY++)
        {
            RenderMonochromeRow(operation, sourceY);
        }
        RasterOperationCount++;
    }

    void RenderMonochromeRow(
        AsicCommandPortMonochromeOperation operation,
        int sourceY)
    {
        for (var sourceX = 0; sourceX < operation.Bitmap.Width; sourceX++)
        {
            RenderMonochromePixel(operation, sourceX, sourceY);
        }
    }

    void RenderMonochromePixel(
        AsicCommandPortMonochromeOperation operation,
        int sourceX,
        int sourceY)
    {
        var x = operation.DestinationX + sourceX;
        var y = operation.DestinationY + sourceY;
        if (x < operation.Destination.Left || x >= operation.Destination.Right ||
            y < operation.Destination.Top || y >= operation.Destination.Bottom)
        {
            return;
        }

        // The bitmap is arranged as 8-row vertical pages. Each page contains
        // one byte per x coordinate, and bit zero is its top row.
        var packed = _cpu.Data[operation.Bitmap.Address +
            sourceY / 8 * operation.Bitmap.Width + sourceX];
        if ((packed & (1 << (sourceY & 7))) == 0)
        {
            return;
        }

        _cpu.Data[operation.Destination.Address +
            y * operation.Destination.Width + x] = _foreground;
        PixelWriteCount++;
    }

    void ExecuteBlt(ReadOnlySpan<byte> packet)
    {
        if (!TryCreateBltRequest(packet, out var request))
        {
            RejectedPacketCount++;
            return;
        }

        if (request.Width == 0 || request.Height == 0)
        {
            return;
        }

        if (!HasValidBltSource(request))
        {
            RejectedPacketCount++;
            return;
        }

        CompleteBlt(request, ClipBlt(request, _destination!.Value), _destination.Value);
    }

    void CompleteBlt(
        AsicCommandPortBltRequest request,
        AsicCommandPortRectangle rectangle,
        AsicCommandPortSurface destination)
    {
        if (rectangle.IsEmpty)
        {
            return;
        }

        RenderBlt(request, rectangle, destination);
        RasterOperationCount++;
    }

    bool TryCreateBltRequest(
        ReadOnlySpan<byte> packet,
        out AsicCommandPortBltRequest request)
    {
        request = new(
            packet[1],
            packet[2],
            packet[5],
            packet[6],
            packet[7],
            packet[8],
            packet[4]);
        return _destination is not null && packet[3] == BlitterMode &&
            (!DependsOnPattern(request.RasterOperation) || _patternAddress is not null) &&
            (!DependsOnSource(request.RasterOperation) || _source is not null);
    }

    bool HasValidBltSource(AsicCommandPortBltRequest request)
    {
        if (_source is not { } source ||
            !DependsOnSource(request.RasterOperation))
        {
            return true;
        }

        return request.SourceX + request.Width <= source.Stride &&
            source.Address +
                (long)(request.SourceY + request.Height - 1) * source.Stride +
                request.SourceX + request.Width <= _cpu.Data.Length;
    }

    static AsicCommandPortRectangle ClipBlt(
        AsicCommandPortBltRequest request,
        AsicCommandPortSurface destination) => new(
            Math.Max(request.DestinationX, destination.Left),
            Math.Max(request.DestinationY, destination.Top),
            Math.Min(request.DestinationX + request.Width, destination.Right),
            Math.Min(request.DestinationY + request.Height, destination.Bottom));

    void RenderBlt(
        AsicCommandPortBltRequest request,
        AsicCommandPortRectangle rectangle,
        AsicCommandPortSurface destination)
    {
        for (var y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            RenderBltRow(request, rectangle, destination, y);
        }
    }

    void RenderBltRow(
        AsicCommandPortBltRequest request,
        AsicCommandPortRectangle rectangle,
        AsicCommandPortSurface destination,
        int y)
    {
        var offsetY = y - request.DestinationY;
        for (var x = rectangle.Left; x < rectangle.Right; x++)
        {
            RenderBltPixel(request, destination, x, y);
        }
    }

    void RenderBltPixel(
        AsicCommandPortBltRequest request,
        AsicCommandPortSurface destination,
        int x,
        int y)
    {
        var offsetY = y - request.DestinationY;
        var patternY = (request.SourceY + offsetY) & (PatternHeight - 1);
        var offsetX = x - request.DestinationX;
        var patternX = (request.SourceX + offsetX) & (PatternWidth - 1);
        var pattern = _patternAddress is int patternAddress
            ? _cpu.Data[patternAddress + patternY * PatternWidth + patternX]
            : (byte)0;
        var sourceValue = _source is { } sourceSurface
            ? _cpu.Data[sourceSurface.Address +
                (request.SourceY + offsetY) * sourceSurface.Stride +
                request.SourceX + offsetX]
            : (byte)0;
        var destinationAddress = destination.Address + y * destination.Width + x;
        _cpu.Data[destinationAddress] = ApplyRop(
            request.RasterOperation,
            pattern,
            sourceValue,
            _cpu.Data[destinationAddress]);
        PixelWriteCount++;
    }

    bool ContainsSurface(int address, int stride, int right, int bottom)
    {
        var lastAddress = (long)address + (long)(bottom - 1) * stride + right - 1;
        return address >= GraphicsRamBase && lastAddress < _cpu.Data.Length;
    }

    static int PacketLength(byte opcode) => opcode switch
    {
        0x05 => 3,
        0x06 => 5,
        0x0a => 4,
        0x0b => 2,
        0x0c => 2,
        0x0d => 1,
        0x0e => 1,
        0x0f => 5,
        0x10 => 12,
        0x11 => 9,
        0x12 => 3,
        0x15 => 1,
        0x4c => 3,
        0x80 => 3,
        _ => 0,
    };

    static bool DependsOnPattern(byte rop) => DependsOnInput(rop, 4);

    static bool DependsOnSource(byte rop) => DependsOnInput(rop, 2);

    static bool DependsOnInput(byte rop, int inputMask)
    {
        for (var inputs = 0; inputs < 8; inputs++)
        {
            if ((inputs & inputMask) != 0)
            {
                continue;
            }
            if (((rop >> inputs) & 1) != ((rop >> (inputs | inputMask)) & 1))
            {
                return true;
            }
        }
        return false;
    }

    static byte ApplyRop(byte rop, byte pattern, byte source, byte destination)
    {
        byte result = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var input = (((pattern >> bit) & 1) << 2) |
                (((source >> bit) & 1) << 1) |
                ((destination >> bit) & 1);
            result |= (byte)(((rop >> input) & 1) << bit);
        }
        return result;
    }
}
