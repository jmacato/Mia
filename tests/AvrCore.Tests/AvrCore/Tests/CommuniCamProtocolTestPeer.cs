// SPDX-License-Identifier: MIT

using System.Text;
using Arm7Core;
using Xunit;

namespace AvrCore.Tests;

internal sealed class CommuniCamProtocolTestPeer : IDisposable
{
    const long SerialPollCycles = 120_000;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicByteChannels _byteChannels;
    readonly ArmModemBus _bus;
    readonly MiaWorker _owner;
    readonly MiaCommuniCamPeripheral _peripheral;
    readonly TestCameraFrameSource _source;

    public CommuniCamProtocolTestPeer()
    {
        _cpu = new Cpu(new byte[0x100], 0x10000);
        _clock = new MiaSystemClock();
        _byteChannels = new AsicByteChannels(_cpu, _clock);
        _bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        _owner = new MiaWorker("CommuniCam test", dedicatedThread: false);
        _peripheral = new MiaCommuniCamPeripheral(
            _byteChannels,
            _bus,
            _owner);
        _source = new TestCameraFrameSource(CreateGradientFrame());
        ConnectPhysicalTransport();
        Assert.Equal(0xa0, SendPacket(BuildConnectPacket())[0]);
        _bus.WriteWord(0x00800508, uint.MaxValue, ArmAccess.None);
    }

    public MiaCameraFrame SourceFrame =>
        _source.Frame ?? throw new InvalidOperationException(
            "The test camera has no frame.");

    public long Cycles => _bus.Cycles;

    public MiaCommuniCamStatus Status => _peripheral.Status;

    public int FrameRequestCount => _source.FrameRequestCount;

    public void Dispose()
    {
        _peripheral.Dispose();
        _source.Dispose();
        _owner.Dispose();
    }

    public void Configure(int width, int height)
    {
        byte[] body = BuildCameraSettings(width, height);
        Assert.Equal(0xa0, SendPacket(BuildPut(body, final: true))[0]);
    }

    public void ConfigureFragmented(int width, int height)
    {
        byte[] body = BuildCameraSettings(width, height);
        int split = body.Length / 2;
        Assert.Equal(
            0x90,
            SendPacket(BuildPut(body.AsSpan(0, split), final: false))[0]);
        Assert.Equal(
            0xa0,
            SendPacket(BuildPut(body.AsSpan(split), final: true))[0]);
    }

    public byte ConfigureRaw(string settings) => SendPacket(
        BuildPut(Encoding.ASCII.GetBytes(settings), final: true))[0];

    public byte PutFragment(byte[] body) => SendPacket(BuildPut(body, final: false))[0];

    public byte ConfigureWithMultipleBodyHeaders(int width, int height)
    {
        byte[] body = BuildCameraSettings(width, height);
        int split = body.Length / 2;
        var packet = new List<byte> { 0x82, 0, 0 };
        AddVariableHeader(packet, 0x01, []);
        AddVariableHeader(packet, 0x48, body.AsSpan(0, split));
        AddVariableHeader(packet, 0x49, body.AsSpan(split));
        SetPacketLength(packet);
        return SendPacket(packet.ToArray())[0];
    }

    public void DelayFrameAvailability(int misses) =>
        _source.DelayFrameAvailability(misses);

    public void PublishSolidFrame(byte pixel)
    {
        const int width = MiaCommuniCamImageProfile.SensorWidth;
        const int height = MiaCommuniCamImageProfile.SensorHeight;
        var pixels = new byte[width * height];
        pixels.AsSpan().Fill(pixel);
        _source.Publish(MiaCameraFrame.Create(
            width,
            height,
            width,
            MiaCameraPixelFormat.Rgb332,
            pixels));
    }

    public void CaptureAndSave()
    {
        _ = GetMonitoring(
            "<monitoring-command version=\"1.0\" take-pic=\"YES\" zoom=\"10\"/>",
            out _);
        _ = GetMonitoring(
            "<monitoring-command version=\"1.0\" take-pic=\"NO\" save=\"YES\" zoom=\"10\"/>",
            out _);
    }

    public byte[] GetCameraInfo() => GetObject(
        BuildGet("x-bt/camera-info", handle: null, description: null),
        out _);

    public byte[] GetMonitoring(string command, out byte[] firstResponse) =>
        GetObject(
            BuildGet(
                "x-bt/imaging-monitoring-image",
                handle: null,
                command),
            out firstResponse);

    public byte[] GetFreshNative(int width, int height) => GetMonitoring(
        FormattableString.Invariant(
            $"<monitoring-command version=\"1.0\" take-pic=\"NO\" send-pixel-size=\"{width}*{height}\" zoom=\"10\"/>"),
        out _);

    public byte SendFreshNativeRequest(string pixelSize) => SendPacket(
        BuildGet(
            "x-bt/imaging-monitoring-image",
            handle: null,
            FormattableString.Invariant(
                $"<monitoring-command version=\"1.0\" take-pic=\"NO\" send-pixel-size=\"{pixelSize}\" zoom=\"10\"/>")))[0];

    public byte[] GetListing() => GetObject(
        BuildGet(
            "x-bt/imaging-images-listing",
            handle: null,
            "<image-handles-descriptor version=\"1.0\"/>"),
        out _);

    public byte[] GetProperties(string handle) => GetObject(
        BuildGet("x-bt/imaging-image-properties", handle, description: null),
        out _);

    public byte[] GetThumbnail(string handle) => GetObject(
        BuildGet("x-bt/imaging-thumbnail", handle, description: null),
        out _);

    public byte[] GetNative(string handle) => GetObject(
        BuildGet(type: null, handle: handle, description: string.Empty),
        out _);

    public byte[] GetVariant(string handle, int width, int height) => GetObject(
        BuildGet(
            type: null,
            handle: handle,
            description: FormattableString.Invariant(
                $"<images-descriptor version=\"1.0\"><image encoding=\"JPEG\" pixel-size=\"{width}*{height}\"/></images-descriptor>")),
        out _);

    public byte Delete(string handle) => SendPacket(
        BuildDelete(handle))[0];

    public byte Abort() => SendPacket([0xff, 0x00, 0x03])[0];

    public byte DisconnectObex() => SendPacket([0x81, 0x00, 0x03])[0];

    public byte ConnectObex() => SendPacket(BuildConnectPacket())[0];

    public byte[] SendChannelControl(byte control)
    {
        byte[] frame = [0xf9, 0x81, control, 0x01, 0, 0xf9];
        frame[4] = CalculateFcs(frame.AsSpan(1, 3));
        WriteSerial(frame);
        return DrainSerialResponse();
    }

    public byte[] BeginThumbnail(string handle) => SendPacket(
        BuildGet("x-bt/imaging-thumbnail", handle, description: null));

    public byte[] SendContinuation() => SendPacket([0x83, 0x00, 0x03]);

    public byte[] BeginFragmentedSettings(int width, int height)
    {
        byte[] body = BuildCameraSettings(width, height);
        return SendPacket(BuildPut(body.AsSpan(0, body.Length / 2), final: false));
    }

    public byte[] SendMalformedHeader() => SendPacket(
        [0x83, 0x00, 0x06, 0x42, 0x00, 0x02]);

    public byte[] SendPacketWithSplitWire(ReadOnlySpan<byte> packet)
    {
        byte[] wire = BuildPhoneFrames(packet);
        int split = wire.Length / 2;
        WriteSerial(wire.AsSpan(0, split));
        Assert.False(_peripheral.HasPendingSerialResponse);
        Assert.Equal(0u, _bus.ReadByte(0x00800b3c, ArmAccess.None));
        WriteSerial(wire.AsSpan(split));
        return DecodeAccessoryFrames(DrainSerialResponse());
    }

    public static byte[] BuildCameraInfoGetPacket() =>
        BuildGet("x-bt/camera-info", handle: null, description: null);

    static MiaCameraFrame CreateGradientFrame()
    {
        const int width = MiaCommuniCamImageProfile.SensorWidth;
        const int height = MiaCommuniCamImageProfile.SensorHeight;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = (byte)(
                    (x * 7 / width << 5) |
                    (y * 7 / height << 2) |
                    ((x + y) * 3 / (width + height)));
            }
        }
        return MiaCameraFrame.Create(
            width,
            height,
            width,
            MiaCameraPixelFormat.Rgb332,
            pixels);
    }

    void ConnectPhysicalTransport()
    {
        _cpu.WriteData(0x090a, 0xff);
        _peripheral.Connect(_source);
        Assert.Empty(DrainControl());
        Assert.NotEqual(
            0u,
            _bus.IrqPending & ArmModemBus.NormalCableDetectInterruptBit);
        _bus.WriteByte(0x00800954, 0, ArmAccess.None);
        _bus.WriteByte(0x00800104, 0x0c, ArmAccess.None);
        _bus.IdleUntilCycle(_bus.Cycles + 16_200_000);
        Assert.Equal("AT&F\r"u8.ToArray(), DrainSerialResponse());
        WriteSerial("AT&F\r\r\nOK\r\n"u8);
        Assert.Equal("AT+IPR=?\r"u8.ToArray(), DrainSerialResponse());
        WriteSerial(
            "AT+IPR=?\r\r\n+IPR: (0,300,1200,2400,4800,9600,"u8);
        WriteSerial(
            "14400,19200,28800,38400,57600,115200,230400),(460800)\r\n\r\nOK\r\n"u8);
        Assert.Equal("AT+IPR=460800\r"u8.ToArray(), DrainSerialResponse());
        WriteSerial("+IPR=460800\r\r\nOK\r\n"u8);
        Assert.Equal("AT+CMUX=?\r"u8.ToArray(), DrainSerialResponse());
        WriteSerial(
            "AT+CMUX=?\r\r\n+CMUX: (0),(0),(1-7),(31),(10),(3),(30),"u8);
        WriteSerial("(10),(1-7)\r\n\r\nOK\r\n"u8);
        Assert.Equal("AT+CMUX=0,0,7,31\r"u8.ToArray(), DrainSerialResponse());
        WriteSerial("AT+CMUX=0,0,7,31\r\r\nOK\r\n"u8);
        _bus.IdleUntilCycle(_bus.Cycles + 130_000);
        Assert.Equal(
            "F9033F011CF9",
            Convert.ToHexString(DrainSerialResponse()));
        WriteSerial([0xf9, 0x03, 0x73, 0x01, 0xd7, 0xf9]);
        _clock.AdvanceBy(600_000);
        Assert.Equal("AAE0450176", Convert.ToHexString(DrainControl()));
        CompleteControlHandshake();
        WriteSerial([0xf9, 0x81, 0x3f, 0x01, 0xab, 0xf9]);
        Assert.Equal(
            "F981730160F9",
            Convert.ToHexString(DrainSerialResponse()));
    }

    void CompleteControlHandshake()
    {
        WriteControl([0xaa, 0x0e, 0x45, 0x02, 0x75, 0x01]);
        _clock.AdvanceBy(600_000);
        Assert.Equal("AA10450178", Convert.ToHexString(DrainControl()));
        WriteControl([0xaa, 0x01, 0x45, 0x01, 0x77]);
        _clock.AdvanceBy(720_000);
        Assert.Equal("AA1045027F00", Convert.ToHexString(DrainControl()));
        WriteControl([0xaa, 0x01, 0x45, 0x02, 0x7f, 0x00]);
        _clock.AdvanceBy(720_000);
        Assert.Equal("AA1045027F02", Convert.ToHexString(DrainControl()));
        WriteControl([0xaa, 0x01, 0x45, 0x02, 0x7f, 0x32]);
        _clock.AdvanceBy(2_040_000);
        Assert.Equal(
            "AA10050D41542A454143533D31372C300D",
            Convert.ToHexString(DrainControl()));
        WriteControl(
            [0xaa, 0x01, 0x05, 0x06, 0x0d, 0x0a, 0x4f, 0x4b, 0x0d, 0x0a]);
        Assert.Equal("AA1F45017E", Convert.ToHexString(DrainControl()));
    }

    byte[] GetObject(ReadOnlySpan<byte> request, out byte[] firstResponse)
    {
        var body = new List<byte>();
        byte[] response = SendPacket(request);
        firstResponse = response;
        while (true)
        {
            Assert.True(MiaCommuniCamObexHeaders.TryParse(
                response,
                3,
                out var headers));
            if (headers?.Body is not null)
            {
                body.AddRange(headers.Body);
            }
            if (response[0] != 0x90)
            {
                Assert.Equal(0xa0, response[0]);
                return body.ToArray();
            }
            response = SendContinuation();
        }
    }

    byte[] SendPacket(ReadOnlySpan<byte> packet)
    {
        WriteSerial(BuildPhoneFrames(packet));
        return DecodeAccessoryFrames(DrainSerialResponse());
    }

    void WriteControl(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            _cpu.WriteData(0x0905, value);
        }
    }

    byte[] DrainControl()
    {
        var bytes = new List<byte>();
        while (_byteChannels.GetReceiveQueueLength(0) != 0)
        {
            bytes.Add(_cpu.ReadData(0x0905));
        }
        return bytes.ToArray();
    }

    void WriteSerial(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            _bus.WriteByte(0x00800b20, value, ArmAccess.None);
        }
    }

    byte[] DrainSerialResponse()
    {
        var bytes = new List<byte>();
        while (_peripheral.HasPendingSerialResponse ||
            _bus.ReadByte(0x00800b3c, ArmAccess.None) != 0)
        {
            if (_bus.ReadByte(0x00800b3c, ArmAccess.None) == 0)
            {
                _bus.IdleUntilCycle(_bus.Cycles + SerialPollCycles);
            }
            while (_bus.ReadByte(0x00800b3c, ArmAccess.None) != 0)
            {
                bytes.Add((byte)_bus.ReadByte(0x00800b30, ArmAccess.None));
            }
        }
        return bytes.ToArray();
    }

    static byte[] BuildConnectPacket() =>
        [0x80, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00];

    static byte[] BuildCameraSettings(int width, int height) =>
        Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"<camera-settings version=\"1.0\" white-balance=\"OFF\" color-compensation=\"13\" fun-layer=\"0\"><monitoring-format encoding=\"EBMP\" pixel-size=\"80*60\" color-depth=\"8\"/>\r\n<thumbnail-format encoding=\"EBMP\" pixel-size=\"101*80\" color-depth=\"8\"/>\r\n<native-format encoding=\"\" pixel-size=\"{width}*{height}\"/>\r\n</camera-settings>\r\n"));

    static byte[] BuildPut(ReadOnlySpan<byte> body, bool final)
    {
        var packet = new List<byte> { final ? (byte)0x82 : (byte)0x02, 0, 0 };
        AddVariableHeader(packet, 0x01, []);
        AddVariableHeader(packet, final ? (byte)0x49 : (byte)0x48, body);
        SetPacketLength(packet);
        return packet.ToArray();
    }

    static byte[] BuildDelete(string handle)
    {
        var packet = new List<byte> { 0x82, 0, 0 };
        AddUnicodeHeader(packet, 0x30, handle);
        SetPacketLength(packet);
        return packet.ToArray();
    }

    static byte[] BuildGet(string? type, string? handle, string? description)
    {
        var packet = new List<byte> { 0x83, 0, 0 };
        if (handle is not null)
        {
            AddUnicodeHeader(packet, 0x30, handle);
        }
        if (description is not null)
        {
            AddVariableHeader(
                packet,
                0x71,
                Encoding.ASCII.GetBytes(description));
        }
        if (type is not null)
        {
            AddVariableHeader(
                packet,
                0x42,
                Encoding.ASCII.GetBytes(type + '\0'));
        }
        SetPacketLength(packet);
        return packet.ToArray();
    }

    static void AddUnicodeHeader(List<byte> packet, byte id, string value)
    {
        byte[] encoded = Encoding.BigEndianUnicode.GetBytes(value + '\0');
        AddVariableHeader(packet, id, encoded);
    }

    static void AddVariableHeader(
        List<byte> packet,
        byte id,
        ReadOnlySpan<byte> value)
    {
        int length = checked(value.Length + 3);
        packet.Add(id);
        packet.Add(checked((byte)(length >> 8)));
        packet.Add((byte)(length & byte.MaxValue));
        foreach (byte item in value)
        {
            packet.Add(item);
        }
    }

    static void SetPacketLength(List<byte> packet)
    {
        packet[1] = checked((byte)(packet.Count >> 8));
        packet[2] = (byte)(packet.Count & byte.MaxValue);
    }

    static byte[] BuildPhoneFrames(ReadOnlySpan<byte> information)
    {
        var wire = new List<byte>();
        for (var offset = 0; offset < information.Length; offset += 31)
        {
            int count = Math.Min(31, information.Length - offset);
            var frame = new byte[count + 6];
            frame[0] = 0xf9;
            frame[1] = 0x81;
            frame[2] = 0xef;
            frame[3] = checked((byte)(count * 2 + 1));
            information.Slice(offset, count).CopyTo(frame.AsSpan(4));
            frame[^2] = CalculateFcs(frame.AsSpan(1, 3));
            frame[^1] = 0xf9;
            wire.AddRange(frame);
        }
        return wire.ToArray();
    }

    static byte[] DecodeAccessoryFrames(ReadOnlySpan<byte> wire)
    {
        var information = new List<byte>();
        int offset = 0;
        while (offset < wire.Length)
        {
            Assert.Equal(0xf9, wire[offset]);
            int length = wire[offset + 3] >> 1;
            Assert.Equal(0x83, wire[offset + 1]);
            Assert.Equal(0xef, wire[offset + 2]);
            information.AddRange(wire.Slice(offset + 4, length).ToArray());
            Assert.Equal(0xf9, wire[offset + length + 5]);
            offset += length + 6;
        }
        return information.ToArray();
    }

    static byte CalculateFcs(ReadOnlySpan<byte> bytes)
    {
        byte fcs = 0xff;
        foreach (byte next in bytes)
        {
            byte tableIndex = (byte)(fcs ^ next);
            for (var bit = 0; bit < 8; bit++)
            {
                tableIndex = (tableIndex & 1) != 0
                    ? (byte)((tableIndex >> 1) ^ 0xe0)
                    : (byte)(tableIndex >> 1);
            }
            fcs = tableIndex;
        }
        return (byte)(0xff - fcs);
    }
}
