// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Mia.Emulator.Asic;
using Mia.Emulator.Modem;
using Mia.Emulator.Runtime;

namespace Mia.Emulator.Camera;

/// <summary>
/// Firmware-visible Ericsson CommuniCam peer. Attachment uses ASIC byte
/// channel 0, while image traffic uses the normal-cable LLRS232 path through
/// ARM FIFO3, TS 07.10 DLCI 32, and OBEX.
/// </summary>
internal sealed class MiaCommuniCamPeripheral : IDisposable
{
    const int MaximumControlFrameLength = 260;
    const int MaximumSerialFrameLength = 260;
    const int MaximumObexPacketLength = 0x2000;
    const int MaximumStoredImages = 10;
    const int InitialFreeMemoryKilobytes = 591;
    const long ControlByteCycles = 120_000;
    const long ExternalSerialStartupDelayCycles = 16_200_000;
    const long RawToMultiplexedDelayCycles = 130_000;
    const int ExternalSerialFrameBits = 10;
    const int InitialExternalSerialBaud = 9_600;
    const int MultiplexedExternalSerialBaud = 460_800;
    const long InitialExternalSerialCharacterCycles =
        (MiaSystemClock.AsicCyclesPerSecond * ExternalSerialFrameBits +
            InitialExternalSerialBaud - 1) /
        InitialExternalSerialBaud;
    const long MultiplexedExternalSerialCharacterCycles =
        (MiaSystemClock.AsicCyclesPerSecond * ExternalSerialFrameBits +
            MultiplexedExternalSerialBaud - 1) /
        MultiplexedExternalSerialBaud;
    const long MonitoringFramePeriodCycles =
        MiaSystemClock.AsicCyclesPerSecond /
        MiaCommuniCamImageProfile.MonitoringFramesPerSecond;
    const long FrameAvailabilityPollCycles =
        MiaSystemClock.AsicCyclesPerSecond / 10;

    static ReadOnlySpan<byte> InitialControlFrame => [0xaa, 0xe0, 0x45, 0x01, 0x76];
    static ReadOnlySpan<byte> HandshakeReplyOne => [0xaa, 0x0e, 0x45, 0x02, 0x75, 0x01];
    static ReadOnlySpan<byte> HandshakeRequestTwo => [0xaa, 0x10, 0x45, 0x01, 0x78];
    static ReadOnlySpan<byte> HandshakeReplyTwo => [0xaa, 0x01, 0x45, 0x01, 0x77];
    static ReadOnlySpan<byte> HandshakeRequestThree => [0xaa, 0x10, 0x45, 0x02, 0x7f, 0x00];
    static ReadOnlySpan<byte> HandshakeReplyThree => [0xaa, 0x01, 0x45, 0x02, 0x7f, 0x00];
    static ReadOnlySpan<byte> HandshakeRequestFour => [0xaa, 0x10, 0x45, 0x02, 0x7f, 0x02];
    static ReadOnlySpan<byte> HandshakeReplyFour => [0xaa, 0x01, 0x45, 0x02, 0x7f, 0x32];
    static ReadOnlySpan<byte> LaunchCameraFrame =>
    [
        0xaa, 0x10, 0x05, 0x0d,
        0x41, 0x54, 0x2a, 0x45, 0x41, 0x43, 0x53,
        0x3d, 0x31, 0x37, 0x2c, 0x30, 0x0d,
    ];
    static ReadOnlySpan<byte> PhoneOkFrame =>
        [0xaa, 0x01, 0x05, 0x06, 0x0d, 0x0a, 0x4f, 0x4b, 0x0d, 0x0a];
    static ReadOnlySpan<byte> PhoneIdleFrame => [0xaa, 0x0f, 0x45, 0x01, 0x7e];
    static ReadOnlySpan<byte> AccessoryIdleFrame => [0xaa, 0x1f, 0x45, 0x01, 0x7e];
    static ReadOnlySpan<byte> ResetCommand => "AT&F\r"u8;
    static ReadOnlySpan<byte> BaudRateQueryCommand => "AT+IPR=?\r"u8;
    static ReadOnlySpan<byte> BaudRateCommand => "AT+IPR=460800\r"u8;
    static ReadOnlySpan<byte> CmuxQueryCommand => "AT+CMUX=?\r"u8;
    static ReadOnlySpan<byte> CmuxCommand => "AT+CMUX=0,0,7,31\r"u8;
    static ReadOnlySpan<byte> SuccessfulAtReplyTail => "OK\r\n"u8;
    static ReadOnlySpan<byte> CameraInfoType => "x-bt/camera-info\0"u8;
    static ReadOnlySpan<byte> MonitoringImageType =>
        "x-bt/imaging-monitoring-image\0"u8;
    static ReadOnlySpan<byte> ImagesListingType =>
        "x-bt/imaging-images-listing\0"u8;
    static ReadOnlySpan<byte> ThumbnailType =>
        "x-bt/imaging-thumbnail\0"u8;
    static ReadOnlySpan<byte> ImagePropertiesType =>
        "x-bt/imaging-image-properties\0"u8;
    static ReadOnlySpan<byte> TakePictureYes => "take-pic=\"YES\""u8;
    static ReadOnlySpan<byte> SavePictureYes => "save=\"YES\""u8;

    static readonly (byte[] Request, byte[] Response, bool QueueImmediately)[]
        ControlExchanges =
        [
            (HandshakeReplyOne.ToArray(), HandshakeRequestTwo.ToArray(), false),
            (HandshakeReplyTwo.ToArray(), HandshakeRequestThree.ToArray(), false),
            (HandshakeReplyThree.ToArray(), HandshakeRequestFour.ToArray(), false),
            (HandshakeReplyFour.ToArray(), LaunchCameraFrame.ToArray(), false),
            (PhoneOkFrame.ToArray(), AccessoryIdleFrame.ToArray(), true),
            (PhoneIdleFrame.ToArray(), AccessoryIdleFrame.ToArray(), true),
        ];

    static readonly (byte[] Type, MiaCommuniCamObexGetKind Kind)[] ObexGetTypes =
    [
        (CameraInfoType.ToArray(), MiaCommuniCamObexGetKind.CameraInfo),
        (ImagesListingType.ToArray(), MiaCommuniCamObexGetKind.ImagesListing),
        (ImagePropertiesType.ToArray(), MiaCommuniCamObexGetKind.ImageProperties),
        (ThumbnailType.ToArray(), MiaCommuniCamObexGetKind.Thumbnail),
        (MonitoringImageType.ToArray(), MiaCommuniCamObexGetKind.MonitoringImage),
    ];

    readonly AsicByteChannels _byteChannels;
    readonly ArmModemBus _bus;
    readonly MiaWorker _owner;
    readonly List<byte> _controlPhoneBytes = [];
    readonly List<byte> _rawPhoneBytes = [];
    readonly List<byte> _serialPhoneFrame = [];
    readonly List<byte> _obexPhoneBytes = [];
    readonly Queue<MiaCommuniCamSerialTransmission>
        _serialTransmissionQueue = [];
    readonly List<MiaCommuniCamStoredImage> _storedImages = [];
    readonly List<byte> _obexPutBody = [];
    IDisposable? _cmuxStartEvent;
    IDisposable? _serialResponseEvent;
    IDisposable? _sabmStartEvent;
    IDisposable? _monitoringFrameEvent;
    IMiaCameraFrameSource? _frameSource;
    MiaCameraFrame? _capturedFrame;
    byte[]? _obexResponseObject;
    byte[]? _obexResponseFirstHeaders;
    byte[]? _activeSerialTransmission;
    int _obexResponseObjectOffset;
    bool _obexResponseIsMonitoringFrame;
    bool _obexResponseFirstPart;
    bool _obexResponseIncludesLength;
    bool _pendingTakePicture;
    bool _pendingSavePicture;
    bool _pendingNativeImage;
    bool _obexPutInProgress;
    bool _obexSessionConnected;
    string? _obexPutImageHandle;
    int _maximumObexResponsePacketLength = 512;
    int _nativeWidth = 80;
    int _nativeHeight = 60;
    int _pendingNativeWidth;
    int _pendingNativeHeight;
    int _nextImageHandle = 1;
    int _activeSerialTransmissionOffset;
    long _activeSerialCharacterCycles;
    long _nextMonitoringFrameCycle;
    int _initialControlBytesRead;
    long _framesSent;
    bool _connected;
    bool _cableConnected;
    bool _controlHandshakeStarted;
    MiaCommuniCamRawSerialPhase _rawSerialPhase;
    bool _multiplexed;
    bool _serialFrameOpen;
    bool _disposed;
    MiaCommuniCamStatus _status = MiaCommuniCamStatus.Disconnected;

    public MiaCommuniCamPeripheral(
        AsicByteChannels byteChannels,
        ArmModemBus bus,
        MiaWorker owner)
    {
        _byteChannels = byteChannels;
        _bus = bus;
        _owner = owner;
        _byteChannels.ByteTransmitted += ObserveControlByteTransmitted;
        _byteChannels.ByteReceived += ObserveControlByteReceived;
        _bus.ExternalSerialByteTransmitted += ObserveSerialByteTransmitted;
        _bus.MmioAccessed += ObserveMmio;
    }

    public event Action<MiaCommuniCamStatus>? StatusChanged;

    public MiaCommuniCamStatus Status => _status;

    internal bool HasPendingSerialResponse =>
        _activeSerialTransmission is not null ||
        _serialResponseEvent is not null ||
        _monitoringFrameEvent is not null ||
        _serialTransmissionQueue.Count != 0;

    public void Connect(IMiaCameraFrameSource frameSource)
    {
        ArgumentNullException.ThrowIfNull(frameSource);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected)
        {
            return;
        }

        ResetProtocolState();
        _frameSource = frameSource;
        _connected = true;
        _byteChannels.SetAccessoryConnected(0, true);
        PublishStatus(
            MiaCommuniCamState.AccessoryHandshake,
            $"CommuniCam connected to {frameSource.DisplayName}");
        // The accessory connector drives normal-cable detect immediately.
        // Its AVR identification handshake runs concurrently and completes
        // later; delaying this edge until EACS acknowledgement causes the
        // native camera actor to miss the TS 07.10 channel-open window.
        AttachCable();
    }

    public void Disconnect()
    {
        if (!_connected && !_cableConnected)
        {
            return;
        }

        _connected = false;
        _frameSource = null;
        _byteChannels.SetAccessoryConnected(0, false);
        if (_cableConnected)
        {
            _bus.SetNormalCableConnected(false);
        }
        _cableConnected = false;
        _byteChannels.ClearReceivedBytes(0);
        _bus.ClearExternalSerialReceivedBytes();
        ResetProtocolState();
        _status = MiaCommuniCamStatus.Disconnected;
        StatusChanged?.Invoke(_status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Disconnect();
        _byteChannels.ByteTransmitted -= ObserveControlByteTransmitted;
        _byteChannels.ByteReceived -= ObserveControlByteReceived;
        _bus.ExternalSerialByteTransmitted -= ObserveSerialByteTransmitted;
        _bus.MmioAccessed -= ObserveMmio;
        _disposed = true;
    }

    void ObserveControlByteTransmitted(int channel, byte value)
    {
        if (channel == 0)
        {
            _owner.Post(() => ProcessControlByte(value));
        }
    }

    void ObserveControlByteReceived(int channel, byte value)
    {
        if (channel == 0)
        {
            _owner.Post(ProcessControlByteReceived);
        }
    }

    void ProcessControlByteReceived()
    {
        if (!_connected || _initialControlBytesRead >= InitialControlFrame.Length)
        {
            return;
        }
        _initialControlBytesRead++;
        if (_initialControlBytesRead == InitialControlFrame.Length)
        {
            // The receive event is raised by the MMIO read, before the native
            // parser has built its reply. The measured hardware-ready edge is
            // later than that read. The native trace reaches its completed
            // parser state about 108,000 ASIC cycles after the final byte, so
            // leave 10 ms for the parser task to publish the response buffer.
            _byteChannels.ScheduleAccessoryTransmitReady(0, 130_000);
        }
    }

    void ProcessControlByte(byte value)
    {
        if (!TryAppendControlByte(value) ||
            !TryTakeControlFrame(out byte[] frame))
        {
            return;
        }

        ProcessControlFrame(frame);
    }

    bool TryAppendControlByte(byte value)
    {
        if (!_connected || (_controlPhoneBytes.Count == 0 && value != 0xaa))
        {
            return false;
        }

        _controlPhoneBytes.Add(value);
        if (_controlPhoneBytes.Count > MaximumControlFrameLength)
        {
            _controlPhoneBytes.Clear();
            return false;
        }
        return true;
    }

    bool TryTakeControlFrame(out byte[] frame)
    {
        frame = [];
        if (_controlPhoneBytes.Count < 4)
        {
            return false;
        }

        int length = _controlPhoneBytes[3];
        if (_controlPhoneBytes.Count != length + 4)
        {
            return false;
        }

        frame = _controlPhoneBytes.ToArray();
        _controlPhoneBytes.Clear();
        return true;
    }

    void ProcessControlFrame(ReadOnlySpan<byte> frame)
    {
        foreach ((byte[] request, byte[] response, bool queueImmediately) in ControlExchanges)
        {
            if (!frame.SequenceEqual(request))
            {
                continue;
            }
            SendControlExchangeResponse(response, queueImmediately);
            return;
        }
    }

    void SendControlExchangeResponse(
        ReadOnlySpan<byte> response,
        bool queueImmediately)
    {
        if (queueImmediately)
        {
            QueueControlBytes(response);
            return;
        }
        ScheduleControlFrame(response);
    }

    void AttachCable()
    {
        if (_cableConnected)
        {
            return;
        }
        _cableConnected = true;
        _bus.SetNormalCableConnected(true);
        PublishStatus(
            MiaCommuniCamState.CableAttached,
            "CommuniCam cable attached; waiting for LLRS232");
        TryStartCableProtocol();
    }

    void ObserveMmio(ArmModemMmioAccess access)
    {
        if (_connected && _cableConnected && access.IsWrite &&
            access.Address == 0x00800104 && access.Size == 1 &&
            (access.Value & 0x0c) == 0x0c)
        {
            TryStartCableProtocol();
        }
    }

    void TryStartCableProtocol()
    {
        if (_rawSerialPhase != MiaCommuniCamRawSerialPhase.NotStarted ||
            _cmuxStartEvent is not null ||
            !_bus.ExternalSerialPortEnabled)
        {
            return;
        }

        // The ARM opens LLRS232 from the normal-cable interrupt path, then its
        // TS 07.10 application task becomes receptive about 16.18 million ARM
        // cycles later. This is the observed interval from the cable edge at
        // 0x010e30a2 to the state-one write at 0x010d52dc. Keep that firmware
        // scheduling delay outside the accessory protocol implementation.
        _cmuxStartEvent = _bus.SchedulePeripheralEvent(
            _bus.Cycles + ExternalSerialStartupDelayCycles,
            _ => StartRawSerialNegotiation());
    }

    void StartRawSerialNegotiation()
    {
        _cmuxStartEvent = null;
        if (!_connected || !_cableConnected ||
            _rawSerialPhase != MiaCommuniCamRawSerialPhase.NotStarted ||
            !_bus.ExternalSerialPortEnabled)
        {
            return;
        }
        _rawSerialPhase = MiaCommuniCamRawSerialPhase.ResetSent;
        StartSerialTransmission(
            ResetCommand,
            firstByteDelayCycles: 0,
            InitialExternalSerialCharacterCycles);
        PublishStatus(
            MiaCommuniCamState.Multiplexing,
            "CommuniCam negotiating 460.8 kbit/s serial link");
    }

    void ObserveSerialByteTransmitted(byte value)
    {
        if (!_connected)
        {
            return;
        }
        if (_multiplexed)
        {
            ProcessSerialFrameByte(value);
            return;
        }

        ObserveRawSerialByte(value);
    }

    void ObserveRawSerialByte(byte value)
    {
        _rawPhoneBytes.Add(value);
        if (_rawPhoneBytes.Count > 512)
        {
            _rawPhoneBytes.RemoveRange(0, _rawPhoneBytes.Count - 512);
        }
        if (EndsWith(_rawPhoneBytes, SuccessfulAtReplyTail))
        {
            AdvanceRawSerialNegotiation();
        }
    }

    void AdvanceRawSerialNegotiation()
    {
        _rawPhoneBytes.Clear();
        switch (_rawSerialPhase)
        {
            case MiaCommuniCamRawSerialPhase.ResetSent:
                _rawSerialPhase = MiaCommuniCamRawSerialPhase.BaudRateQuerySent;
                StartSerialTransmission(
                    BaudRateQueryCommand,
                    InitialExternalSerialCharacterCycles,
                    InitialExternalSerialCharacterCycles);
                break;
            case MiaCommuniCamRawSerialPhase.BaudRateQuerySent:
                _rawSerialPhase = MiaCommuniCamRawSerialPhase.BaudRateCommandSent;
                StartSerialTransmission(
                    BaudRateCommand,
                    InitialExternalSerialCharacterCycles,
                    InitialExternalSerialCharacterCycles);
                break;
            case MiaCommuniCamRawSerialPhase.BaudRateCommandSent:
                _rawSerialPhase = MiaCommuniCamRawSerialPhase.CmuxQuerySent;
                StartSerialTransmission(
                    CmuxQueryCommand,
                    MultiplexedExternalSerialCharacterCycles,
                    MultiplexedExternalSerialCharacterCycles);
                break;
            case MiaCommuniCamRawSerialPhase.CmuxQuerySent:
                _rawSerialPhase = MiaCommuniCamRawSerialPhase.CmuxCommandSent;
                StartSerialTransmission(
                    CmuxCommand,
                    MultiplexedExternalSerialCharacterCycles,
                    MultiplexedExternalSerialCharacterCycles);
                break;
            case MiaCommuniCamRawSerialPhase.CmuxCommandSent:
                _rawSerialPhase = MiaCommuniCamRawSerialPhase.Multiplexed;
                _multiplexed = true;
                // The final response byte leaves LLRS232 before the phone task
                // publishes its framed-parser state. In the native trace that
                // transition completes about 100,000 ARM cycles later.
                _sabmStartEvent = _bus.SchedulePeripheralEvent(
                    _bus.Cycles + RawToMultiplexedDelayCycles,
                    _ => SendInitialSabm());
                break;
        }
    }

    void ProcessSerialFrameByte(byte value)
    {
        if (value == 0xf9)
        {
            CompleteSerialFrame();
            return;
        }
        if (!_serialFrameOpen)
        {
            return;
        }
        _serialPhoneFrame.Add(value);
        if (_serialPhoneFrame.Count > MaximumSerialFrameLength)
        {
            _serialPhoneFrame.Clear();
            _serialFrameOpen = false;
        }
    }

    void CompleteSerialFrame()
    {
        if (_serialFrameOpen && _serialPhoneFrame.Count != 0)
        {
            ProcessSerialFrame(_serialPhoneFrame);
        }
        _serialPhoneFrame.Clear();
        _serialFrameOpen = true;
    }

    void ProcessSerialFrame(List<byte> frame)
    {
        if (!TryReadSerialFrame(frame, out MiaCommuniCamReceivedSerialFrame received))
        {
            return;
        }

        switch ((received.Address, received.Control))
        {
            case (0x03, 0x73) when !_controlHandshakeStarted:
                StartControlHandshake();
                break;
            case (0x81, 0x3f):
                ResetSerialChannel();
                AcknowledgeSerialChannel();
                break;
            case (0x81, 0x53):
                // Leaving the camera menu closes its DLCI, not the physical
                // accessory. Acknowledge DISC so the handset can reopen it.
                ResetSerialChannel();
                AcknowledgeSerialChannel();
                PublishStatus(MiaCommuniCamState.Multiplexing,
                    "CommuniCam attached; camera channel closed");
                break;
            case (0x81, 0xef) when !received.Information.IsEmpty:
                _obexPhoneBytes.AddRange(received.Information.ToArray());
                ProcessObexPackets();
                break;
        }
    }

    void StartControlHandshake()
    {
        _controlHandshakeStarted = true;
        ScheduleControlFrame(InitialControlFrame);
    }

    void ResetSerialChannel()
    {
        ResetObexOperations();
        _obexPhoneBytes.Clear();
        _obexSessionConnected = false;
    }

    void AcknowledgeSerialChannel()
    {
        StartSerialTransmission(
            BuildTs0710ControlFrame(0x81, 0x73),
            MultiplexedExternalSerialCharacterCycles,
            MultiplexedExternalSerialCharacterCycles);
    }

    static bool TryReadSerialFrame(
        List<byte> frame,
        out MiaCommuniCamReceivedSerialFrame received)
    {
        received = default;
        if (frame.Count < 4 || (frame[2] & 1) == 0)
        {
            return false;
        }

        int informationLength = frame[2] >> 1;
        Span<byte> bytes = CollectionsMarshal.AsSpan(frame);
        if (frame.Count != informationLength + 4 ||
            CalculateTs0710Fcs(bytes[..3]) != frame[^1])
        {
            return false;
        }

        received = new MiaCommuniCamReceivedSerialFrame(
            frame[0],
            frame[1],
            bytes.Slice(3, informationLength));
        return true;
    }

    void ProcessObexPackets()
    {
        while (_obexPhoneBytes.Count >= 3)
        {
            int length = _obexPhoneBytes[1] << 8 | _obexPhoneBytes[2];
            if (length is < 3 or > MaximumObexPacketLength)
            {
                _obexPhoneBytes.Clear();
                SendObexResponse([0xc0, 0x00, 0x03]);
                return;
            }
            if (_obexPhoneBytes.Count < length)
            {
                return;
            }

            byte[] packet = _obexPhoneBytes.GetRange(0, length).ToArray();
            _obexPhoneBytes.RemoveRange(0, length);
            ProcessObexPacket(packet);
        }
    }

    void ProcessObexPacket(ReadOnlySpan<byte> packet)
    {
        switch (packet[0])
        {
            case 0x80:
                ProcessObexConnect(packet);
                break;
            case 0x02:
            case 0x82:
                ProcessObexPut(packet);
                break;
            case 0x81:
                ResetObexOperations();
                _obexSessionConnected = false;
                SendObexResponse([0xa0, 0x00, 0x03]);
                break;
            case 0xff:
                ResetObexOperations();
                SendObexResponse([0xa0, 0x00, 0x03]);
                break;
            case 0x83:
                ProcessObexGet(packet);
                break;
            default:
                SendObexResponse([0xc0, 0x00, 0x03]);
                break;
        }
    }

    void ProcessObexConnect(ReadOnlySpan<byte> packet)
    {
        ResetObexOperations();
        if (packet.Length < 7)
        {
            SendObexResponse([0xc0, 0x00, 0x03]);
            return;
        }

        _maximumObexResponsePacketLength = Math.Clamp(
            packet[5] << 8 | packet[6],
            255,
            512);
        _obexSessionConnected = true;
        SendObexResponse(
        [
            0xa0, 0x00, 0x07, 0x10, 0x00,
            checked((byte)(_maximumObexResponsePacketLength >> 8)),
            (byte)(_maximumObexResponsePacketLength & byte.MaxValue),
        ]);
        PublishStatus(
            MiaCommuniCamState.ObexConnected,
            "CommuniCam OBEX session ready");
    }

    void ProcessObexPut(ReadOnlySpan<byte> packet)
    {
        if (!PrepareObexPut())
        {
            return;
        }
        bool final = packet[0] == 0x82;
        if (!MiaCommuniCamObexHeaders.TryParse(packet, 3, out var headers) ||
            headers is null)
        {
            ClearObexPut();
            SendObexResponse([0xc0, 0x00, 0x03]);
            return;
        }

        if ((headers.Body?.Length ?? 0) > MaximumObexPacketLength - _obexPutBody.Count)
        {
            ClearObexPut();
            SendObexResponse([0xcd, 0x00, 0x03]);
            return;
        }
        AccumulateObexPut(headers);
        if (!final)
        {
            SendObexResponse([0x90, 0x00, 0x03]);
            return;
        }

        byte responseCode = CompleteObexPut();
        ClearObexPut();
        SendObexResponse([responseCode, 0x00, 0x03]);
    }

    bool PrepareObexPut()
    {
        if (!_obexSessionConnected)
        {
            SendObexResponse([0xc1, 0x00, 0x03]);
            return false;
        }
        if (_obexResponseObject is not null || _monitoringFrameEvent is not null)
        {
            ResetObexOperations();
            SendObexResponse([0xc9, 0x00, 0x03]);
            return false;
        }
        return true;
    }

    void AccumulateObexPut(MiaCommuniCamObexHeaders headers)
    {
        if (!_obexPutInProgress)
        {
            ClearObexPut();
            _obexPutInProgress = true;
        }
        if (!string.IsNullOrEmpty(headers.ImageHandle))
        {
            _obexPutImageHandle = headers.ImageHandle;
        }
        if (headers.Body is not null)
        {
            _obexPutBody.AddRange(headers.Body);
        }
    }

    byte CompleteObexPut()
    {
        if (_obexPutBody.Count != 0)
        {
            return TryApplyCameraSettings(
                CollectionsMarshal.AsSpan(_obexPutBody))
                ? (byte)0xa0
                : (byte)0xc0;
        }
        if (_obexPutImageHandle is null)
        {
            return 0xa0;
        }

        int index = _storedImages.FindIndex(
            image => image.Handle == _obexPutImageHandle);
        if (index < 0)
        {
            return 0xc4;
        }
        _storedImages.RemoveAt(index);
        return 0xa0;
    }

    bool TryApplyCameraSettings(ReadOnlySpan<byte> body)
    {
        try
        {
            string xml = Encoding.ASCII.GetString(body);
            XDocument document = XDocument.Parse(xml, LoadOptions.None);
            if (document.Root?.Name.LocalName != "camera-settings")
            {
                return false;
            }
            XElement? monitoringFormat = document
                .Descendants("monitoring-format")
                .SingleOrDefault();
            XElement? thumbnailFormat = document
                .Descendants("thumbnail-format")
                .SingleOrDefault();
            XElement? nativeFormat = document.Descendants("native-format")
                .SingleOrDefault();
            string? nativeEncoding = (string?)nativeFormat?
                .Attribute("encoding");
            if (!MatchesFixedFormat(
                    monitoringFormat,
                    "EBMP",
                    "80*60",
                    "8") ||
                !MatchesFixedFormat(
                    thumbnailFormat,
                    "EBMP",
                    "101*80",
                    "8") ||
                nativeFormat is null ||
                nativeEncoding is not ("" or "JPEG") ||
                !MiaCommuniCamImageProfile.TryParseNativeSize(
                    (string?)nativeFormat.Attribute("pixel-size"),
                    out int width,
                    out int height))
            {
                return false;
            }
            _nativeWidth = width;
            _nativeHeight = height;
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    static bool MatchesFixedFormat(
        XElement? format,
        string encoding,
        string pixelSize,
        string colorDepth) =>
        format is not null &&
        (string?)format.Attribute("encoding") == encoding &&
        (string?)format.Attribute("pixel-size") == pixelSize &&
        (string?)format.Attribute("color-depth") == colorDepth;

    void ProcessObexGet(ReadOnlySpan<byte> packet)
    {
        if (!PrepareObexGet())
        {
            return;
        }
        if (packet.Length == 3)
        {
            ContinueObexGet();
            return;
        }

        ProcessNewObexGet(packet);
    }

    bool PrepareObexGet()
    {
        if (!_obexSessionConnected)
        {
            SendObexResponse([0xc1, 0x00, 0x03]);
            return false;
        }
        if (_obexPutInProgress)
        {
            ClearObexPut();
            SendObexResponse([0xc9, 0x00, 0x03]);
            return false;
        }
        return true;
    }

    void ContinueObexGet()
    {
        if (_obexResponseObject is not null)
        {
            SendNextObexObjectPart();
            return;
        }
        SendObexResponse([0xa0, 0x00, 0x03]);
    }

    void ProcessNewObexGet(ReadOnlySpan<byte> packet)
    {
        if (_obexResponseObject is not null || _monitoringFrameEvent is not null)
        {
            SendObexResponse([0xc9, 0x00, 0x03]);
            return;
        }
        if (!MiaCommuniCamObexHeaders.TryParse(packet, 3, out var headers) ||
            headers is null)
        {
            SendObexResponse([0xc0, 0x00, 0x03]);
            return;
        }

        DispatchObexGet(headers);
    }

    void DispatchObexGet(MiaCommuniCamObexHeaders headers)
    {
        switch (ResolveObexGetKind(headers))
        {
            case MiaCommuniCamObexGetKind.CameraInfo:
                StartObexObject(BuildCameraInfo(), isMonitoringFrame: false);
                break;
            case MiaCommuniCamObexGetKind.ImagesListing:
                StartObexObject(
                    BuildImagesListing(),
                    isMonitoringFrame: false,
                    BuildReturnedHandlesHeader(_storedImages.Count));
                break;
            case MiaCommuniCamObexGetKind.ImageProperties:
                SendImageProperties(headers.ImageHandle);
                break;
            case MiaCommuniCamObexGetKind.Thumbnail:
                SendThumbnail(headers.ImageHandle);
                break;
            case MiaCommuniCamObexGetKind.MonitoringImage:
                ScheduleMonitoringFrame(headers.Description);
                break;
            case MiaCommuniCamObexGetKind.StoredImage:
                SendStoredImage(headers.ImageHandle!, headers.Description);
                break;
            default:
                SendObexResponse([0xc0, 0x00, 0x03]);
                break;
        }
    }

    static MiaCommuniCamObexGetKind ResolveObexGetKind(
        MiaCommuniCamObexHeaders headers)
    {
        foreach ((byte[] type, MiaCommuniCamObexGetKind kind) in ObexGetTypes)
        {
            if (headers.MatchesType(type))
            {
                return kind;
            }
        }
        return headers.ImageHandle is not null && headers.Type is null
            ? MiaCommuniCamObexGetKind.StoredImage
            : MiaCommuniCamObexGetKind.Unknown;
    }

    void ScheduleMonitoringFrame(byte[]? description)
    {
        if (_monitoringFrameEvent is not null || _obexResponseObject is not null)
        {
            return;
        }

        ReadOnlySpan<byte> command = description;
        _pendingTakePicture = command.IndexOf(TakePictureYes) >= 0;
        _pendingSavePicture = command.IndexOf(SavePictureYes) >= 0;
        bool requestsNativeImage = command.IndexOf(
            "send-pixel-size=\""u8) >= 0;
        _pendingNativeImage = TryReadPixelSize(
            command,
            "send-pixel-size",
            out _pendingNativeWidth,
            out _pendingNativeHeight);
        if (requestsNativeImage && !_pendingNativeImage)
        {
            ClearPendingMonitoringRequest();
            SendObexResponse([0xc0, 0x00, 0x03]);
            return;
        }

        long dueCycle = Math.Max(_bus.Cycles, _nextMonitoringFrameCycle);
        _nextMonitoringFrameCycle = dueCycle + MonitoringFramePeriodCycles;
        if (dueCycle == _bus.Cycles)
        {
            StartMonitoringFrame();
            return;
        }

        _monitoringFrameEvent = _bus.SchedulePeripheralEvent(
            dueCycle,
            _ =>
            {
                _monitoringFrameEvent = null;
                StartMonitoringFrame();
            });
    }

    void StartMonitoringFrame()
    {
        if (!_connected || !_cableConnected || !_multiplexed)
        {
            return;
        }

        if (!TryGetMonitoringFrame(out MiaCameraFrame? frame))
        {
            return;
        }

        if (_pendingNativeImage)
        {
            SendNativeMonitoringFrame(frame);
            return;
        }

        SaveMonitoringFrameIfRequested(frame);
        ClearPendingMonitoringRequest();
        StartObexObject(
            MiaCommuniCamMonitoringImageEncoder.Encode(frame),
            isMonitoringFrame: true,
            includeLengthHeader: true);
    }

    bool TryGetMonitoringFrame(
        [NotNullWhen(true)] out MiaCameraFrame? frame)
    {
        frame = _capturedFrame;
        if (!_pendingTakePicture && frame is not null)
        {
            return true;
        }
        if (_frameSource is not { IsAvailable: true } frameSource)
        {
            ClearPendingMonitoringRequest();
            SendObexResponse([0xd0, 0x00, 0x03]);
            return false;
        }
        if (!frameSource.TryGetLatestFrame(out frame) || frame is null)
        {
            ScheduleMonitoringFrameRetry();
            return false;
        }

        _capturedFrame = frame;
        return true;
    }

    void ScheduleMonitoringFrameRetry()
    {
        _monitoringFrameEvent = _bus.SchedulePeripheralEvent(
            _bus.Cycles + FrameAvailabilityPollCycles,
            _ =>
            {
                _monitoringFrameEvent = null;
                StartMonitoringFrame();
            });
    }

    void SendNativeMonitoringFrame(MiaCameraFrame frame)
    {
        int nativeWidth = _pendingNativeWidth;
        int nativeHeight = _pendingNativeHeight;
        ClearPendingMonitoringRequest();
        StartObexObject(
            MiaCommuniCamJpegEncoder.Encode(frame, nativeWidth, nativeHeight),
            isMonitoringFrame: false);
    }

    void SaveMonitoringFrameIfRequested(MiaCameraFrame frame)
    {
        if (_pendingSavePicture &&
            _storedImages.Count < MaximumStoredImages)
        {
            _storedImages.Add(new MiaCommuniCamStoredImage(
                _nextImageHandle++,
                CloneFrame(frame),
                _nativeWidth,
                _nativeHeight,
                DateTimeOffset.UtcNow));
        }
    }

    void ClearPendingMonitoringRequest()
    {
        _pendingTakePicture = false;
        _pendingSavePicture = false;
        _pendingNativeImage = false;
        _pendingNativeWidth = 0;
        _pendingNativeHeight = 0;
    }

    byte[] BuildCameraInfo()
    {
        int freeImages = MaximumStoredImages - _storedImages.Count;
        int usedKilobytes = _storedImages.Sum(
            image => (image.Jpeg.Length + 1023) / 1024);
        int freeKilobytes = Math.Max(
            0,
            InitialFreeMemoryKilobytes - usedKilobytes);
        string xml = FormattableString.Invariant(
            $"<camera-info version=\"1.0\" SW-version=\"R1A CXC125496\">\r\n<memory free=\"{freeKilobytes}\" free-images=\"{freeImages}\" stored-images=\"{_storedImages.Count}\" fun-layer=\"10\"/>\r\n</camera-info>\r\n");
        return Encoding.ASCII.GetBytes(xml);
    }

    byte[] BuildImagesListing()
    {
        var xml = new StringBuilder(
            "<images-listing version=\"1.0\">\r\n");
        foreach (MiaCommuniCamStoredImage image in _storedImages)
        {
            _ = xml.Append(FormattableString.Invariant(
                $"<image handle=\"{image.Handle}\" created=\"{image.Created}\"/>\r\n"));
        }
        _ = xml.Append("</images-listing>\r\n");
        return Encoding.ASCII.GetBytes(xml.ToString());
    }

    static byte[] BuildReturnedHandlesHeader(int count) =>
    [
        0x4c, 0x00, 0x07,
        0x01, 0x02, checked((byte)(count >> 8)), checked((byte)count),
    ];

    static MiaCameraFrame CloneFrame(MiaCameraFrame frame) =>
        MiaCameraFrame.Create(
            frame.Width,
            frame.Height,
            frame.Stride,
            frame.PixelFormat,
            frame.Pixels.ToArray());

    void SendThumbnail(string? handle)
    {
        if (!TryGetStoredImage(handle, out var image) || image is null)
        {
            SendObexResponse([0xc4, 0x00, 0x03]);
            return;
        }

        StartObexObject(
            image.Thumbnail,
            isMonitoringFrame: false);
    }

    bool TryGetStoredImage(
        string? handle,
        out MiaCommuniCamStoredImage? image)
    {
        image = null;
        if (string.IsNullOrEmpty(handle))
        {
            return false;
        }

        image = _storedImages.Find(candidate => candidate.Handle == handle);
        return image is not null;
    }

    void SendImageProperties(string? handle)
    {
        if (!TryGetStoredImage(handle, out var image) || image is null)
        {
            SendObexResponse([0xc4, 0x00, 0x03]);
            return;
        }

        var xml = new StringBuilder();
        _ = xml.Append(FormattableString.Invariant(
            $"<image-property version=\"1.0\" handle=\"{image.Handle}\">\r\n"));
        _ = xml.Append(FormattableString.Invariant(
            $"<native-image encoding=\"JPEG\" pixel-size=\"{image.Width}*{image.Height}\"/>\r\n"));
        _ = xml.Append(
            "<variant-image encoding=\"JPEG\" pixel-size=\"80*60\"/>\r\n" +
            "<variant-image encoding=\"JPEG\" pixel-size=\"160*120\"/>\r\n" +
            "<variant-image encoding=\"JPEG\" pixel-size=\"320*240\"/>\r\n" +
            "<variant-image encoding=\"JPEG\" pixel-size=\"640*480\"/>\r\n" +
            "</image-property>\r\n");
        StartObexObject(
            Encoding.ASCII.GetBytes(xml.ToString()),
            isMonitoringFrame: false);
    }

    void SendStoredImage(string handle, byte[]? description)
    {
        if (!TryGetStoredImage(handle, out var image) || image is null)
        {
            SendObexResponse([0xc4, 0x00, 0x03]);
            return;
        }

        int width = image.Width;
        int height = image.Height;
        if (description is { Length: > 0 } &&
            !TryReadPixelSize(
                description,
                "pixel-size",
                out width,
                out height))
        {
            SendObexResponse([0xc0, 0x00, 0x03]);
            return;
        }
        StartObexObject(
            image.GetJpeg(width, height),
            isMonitoringFrame: false);
    }

    static bool TryReadPixelSize(
        ReadOnlySpan<byte> xml,
        string attribute,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        string text = Encoding.ASCII.GetString(xml);
        string prefix = attribute + "=\"";
        int start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }
        start += prefix.Length;
        int end = text.IndexOf('"', start);
        return end > start && MiaCommuniCamImageProfile.TryParseNativeSize(
            text[start..end],
            out width,
            out height);
    }

    void ResetObexOperations()
    {
        _monitoringFrameEvent?.Dispose();
        _monitoringFrameEvent = null;
        ClearPendingMonitoringRequest();
        ClearObexResponseObject();
        ClearObexPut();
    }

    void ClearObexPut()
    {
        _obexPutBody.Clear();
        _obexPutImageHandle = null;
        _obexPutInProgress = false;
    }

    void StartObexObject(
        byte[] value,
        bool isMonitoringFrame,
        ReadOnlySpan<byte> firstHeaders = default,
        bool includeLengthHeader = false)
    {
        _obexResponseObject = value;
        _obexResponseFirstHeaders = firstHeaders.ToArray();
        _obexResponseObjectOffset = 0;
        _obexResponseIsMonitoringFrame = isMonitoringFrame;
        _obexResponseFirstPart = true;
        _obexResponseIncludesLength = includeLengthHeader;
        SendNextObexObjectPart();
    }

    void SendNextObexObjectPart()
    {
        byte[]? responseObject = _obexResponseObject;
        if (responseObject is null)
        {
            return;
        }

        int leadingHeaderLength = _obexResponseFirstPart
            ? (_obexResponseIncludesLength ? 5 : 0) +
                (_obexResponseFirstHeaders?.Length ?? 0)
            : 0;
        int maximumBodyLength =
            _maximumObexResponsePacketLength - 3 - leadingHeaderLength - 3;
        int bodyLength = Math.Min(
            maximumBodyLength,
            responseObject.Length - _obexResponseObjectOffset);
        bool final = _obexResponseObjectOffset + bodyLength ==
            responseObject.Length;
        var response = new byte[3 + leadingHeaderLength + 3 + bodyLength];
        response[0] = final ? (byte)0xa0 : (byte)0x90;
        response[1] = (byte)(response.Length >> 8);
        response[2] = (byte)response.Length;
        int bodyHeaderOffset = WriteLeadingObexHeaders(response, responseObject.Length);
        response[bodyHeaderOffset] = final ? (byte)0x49 : (byte)0x48;
        int headerLength = bodyLength + 3;
        response[bodyHeaderOffset + 1] = (byte)(headerLength >> 8);
        response[bodyHeaderOffset + 2] = (byte)headerLength;
        responseObject.AsSpan(_obexResponseObjectOffset, bodyLength)
            .CopyTo(response.AsSpan(bodyHeaderOffset + 3));
        _obexResponseObjectOffset += bodyLength;
        _obexResponseFirstPart = false;
        SendObexResponse(response);
        FinishObexObjectIfComplete(final);
    }

    int WriteLeadingObexHeaders(byte[] response, int objectLength)
    {
        var offset = 3;
        if (!_obexResponseFirstPart)
        {
            return offset;
        }
        if (_obexResponseIncludesLength)
        {
            WriteObexLengthHeader(response, objectLength);
            offset += 5;
        }
        if (_obexResponseFirstHeaders is not null)
        {
            _obexResponseFirstHeaders.CopyTo(response, offset);
            offset += _obexResponseFirstHeaders.Length;
        }
        return offset;
    }

    static void WriteObexLengthHeader(byte[] response, int objectLength)
    {
        response[3] = 0xc3;
        response[4] = (byte)(objectLength >> 24);
        response[5] = (byte)(objectLength >> 16);
        response[6] = (byte)(objectLength >> 8);
        response[7] = (byte)objectLength;
    }

    void FinishObexObjectIfComplete(bool final)
    {
        if (!final)
        {
            return;
        }

        bool completedMonitoringFrame = _obexResponseIsMonitoringFrame;
        ClearObexResponseObject();
        if (completedMonitoringFrame)
        {
            _framesSent++;
            PublishStatus(
                MiaCommuniCamState.Streaming,
                $"CommuniCam sent frame {_framesSent:n0}");
        }
    }

    void ClearObexResponseObject()
    {
        _obexResponseObject = null;
        _obexResponseFirstHeaders = null;
        _obexResponseObjectOffset = 0;
        _obexResponseIsMonitoringFrame = false;
        _obexResponseFirstPart = false;
        _obexResponseIncludesLength = false;
    }

    void SendObexResponse(ReadOnlySpan<byte> packet)
    {
        var serialBytes = new List<byte>(packet.Length + 64);
        for (var offset = 0; offset < packet.Length; offset += 31)
        {
            int count = Math.Min(31, packet.Length - offset);
            var frame = new byte[count + 6];
            frame[0] = 0xf9;
            frame[1] = 0x83;
            frame[2] = 0xef;
            frame[3] = checked((byte)((count << 1) | 1));
            packet.Slice(offset, count).CopyTo(frame.AsSpan(4));
            frame[^2] = CalculateTs0710Fcs(frame.AsSpan(1, 3));
            frame[^1] = 0xf9;
            serialBytes.AddRange(frame);
        }

        // AT+IPR switches LLRS232 from its 9600-baud boot rate to 460800 baud
        // before TS 07.10 starts. Deliver one framed character at a time so
        // FIFO3 and its interrupt retain their hardware pacing.
        StartSerialTransmission(
            CollectionsMarshal.AsSpan(serialBytes),
            MultiplexedExternalSerialCharacterCycles,
            MultiplexedExternalSerialCharacterCycles);
    }

    void StartSerialTransmission(
        ReadOnlySpan<byte> bytes,
        long firstByteDelayCycles,
        long characterCycles)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var transmission = new MiaCommuniCamSerialTransmission(
            bytes.ToArray(),
            firstByteDelayCycles,
            characterCycles);
        if (_activeSerialTransmission is not null ||
            _serialResponseEvent is not null)
        {
            _serialTransmissionQueue.Enqueue(transmission);
            return;
        }

        BeginSerialTransmission(transmission);
    }

    void BeginSerialTransmission(MiaCommuniCamSerialTransmission transmission)
    {
        _activeSerialTransmission = transmission.Bytes;
        _activeSerialTransmissionOffset = 0;
        _activeSerialCharacterCycles = transmission.CharacterCycles;
        long firstByteDelayCycles = transmission.FirstByteDelayCycles;
        if (firstByteDelayCycles == 0)
        {
            TransmitNextSerialByte();
        }
        else
        {
            ScheduleNextSerialByte(firstByteDelayCycles);
        }
    }

    void ScheduleNextSerialByte(long delayCycles)
    {
        _serialResponseEvent = _bus.SchedulePeripheralEvent(
            _bus.Cycles + delayCycles,
            _ =>
            {
                _serialResponseEvent = null;
                TransmitNextSerialByte();
            });
    }

    void TransmitNextSerialByte()
    {
        if (!_connected || !_cableConnected ||
            !_bus.ExternalSerialPortEnabled ||
            _activeSerialTransmission is null)
        {
            ClearSerialTransmissions();
            return;
        }

        QueueSerialBytes(
            [_activeSerialTransmission[_activeSerialTransmissionOffset++]]);
        if (_activeSerialTransmissionOffset < _activeSerialTransmission.Length)
        {
            ScheduleNextSerialByte(_activeSerialCharacterCycles);
            return;
        }

        CompleteSerialTransmission();
    }

    void CompleteSerialTransmission()
    {
        _activeSerialTransmission = null;
        _activeSerialTransmissionOffset = 0;
        if (_serialTransmissionQueue.TryDequeue(out var next))
        {
            BeginSerialTransmission(next);
        }
    }

    void QueueControlBytes(ReadOnlySpan<byte> values)
    {
        foreach (byte value in values)
        {
            _byteChannels.QueueReceivedByte(0, value);
        }
    }

    void ScheduleControlFrame(ReadOnlySpan<byte> values) =>
        _byteChannels.ScheduleReceivedBytes(
            0,
            values,
            ControlByteCycles,
            ControlByteCycles);

    void QueueSerialBytes(ReadOnlySpan<byte> values) =>
        _bus.QueueExternalSerialReceivedBytes(values);

    void PublishStatus(MiaCommuniCamState state, string message)
    {
        _status = new(state, message, _framesSent);
        StatusChanged?.Invoke(_status);
    }

    void ResetProtocolState()
    {
        _cmuxStartEvent?.Dispose();
        _cmuxStartEvent = null;
        _serialResponseEvent?.Dispose();
        _serialResponseEvent = null;
        ClearSerialTransmissions();
        _sabmStartEvent?.Dispose();
        _sabmStartEvent = null;
        _controlPhoneBytes.Clear();
        _rawPhoneBytes.Clear();
        _serialPhoneFrame.Clear();
        _obexPhoneBytes.Clear();
        ResetObexOperations();
        _obexSessionConnected = false;
        _capturedFrame = null;
        _maximumObexResponsePacketLength = 512;
        _nativeWidth = 80;
        _nativeHeight = 60;
        _nextMonitoringFrameCycle = 0;
        _framesSent = 0;
        _initialControlBytesRead = 0;
        _controlHandshakeStarted = false;
        _rawSerialPhase = MiaCommuniCamRawSerialPhase.NotStarted;
        _multiplexed = false;
        _serialFrameOpen = false;
    }

    void ClearSerialTransmissions()
    {
        _activeSerialTransmission = null;
        _activeSerialTransmissionOffset = 0;
        _activeSerialCharacterCycles = 0;
        _serialTransmissionQueue.Clear();
    }

    void SendInitialSabm()
    {
        _sabmStartEvent = null;
        if (_connected && _cableConnected && _multiplexed)
        {
            StartSerialTransmission(
                BuildTs0710ControlFrame(0x03, 0x3f),
                firstByteDelayCycles: 0,
                MultiplexedExternalSerialCharacterCycles);
        }
    }

    static byte[] BuildTs0710ControlFrame(byte address, byte control)
    {
        var frame = new byte[] { 0xf9, address, control, 0x01, 0x00, 0xf9 };
        frame[4] = CalculateTs0710Fcs(frame.AsSpan(1, 3));
        return frame;
    }

    static byte CalculateTs0710Fcs(ReadOnlySpan<byte> bytes)
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

    static bool EndsWith(List<byte> bytes, ReadOnlySpan<byte> suffix) =>
        bytes.Count >= suffix.Length &&
        CollectionsMarshal.AsSpan(bytes)[(bytes.Count - suffix.Length)..]
            .SequenceEqual(suffix);

}
