// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Infrared;

/// <summary>
/// Framed SIR boundary attached to the modem firmware's native LLIrDA driver.
/// IrLAP, IrLMP, TinyTP, and OBEX remain entirely inside the handset firmware;
/// this adapter only performs the physical BOF/EOF escaping and FCS used by
/// the FIFO3 byte stream.
/// </summary>
internal sealed class ArmModemInfraredPeripheral : IDisposable
{
    internal const int MaximumFrameLength = 4096;
    const byte Bof = 0xc0;
    const byte Eof = 0xc1;
    const byte Escape = 0x7d;
    const ushort InitialFcs = 0xffff;
    const ushort ValidFrameResidue = 0xf0b8;

    readonly ArmModemBus _bus;
    readonly List<byte> _receiveBuffer = [];
    bool _receiving;
    bool _escaped;
    bool _disposed;

    public ArmModemInfraredPeripheral(ArmModem modem)
        : this(modem.Bus)
    {
    }

    public ArmModemInfraredPeripheral(ArmModemBus bus)
    {
        _bus = bus;
        _bus.InfraredByteTransmitted += ObserveTransmittedByte;
        _bus.MmioAccessed += ObserveMmio;
    }

    public bool PortEnabled { get; private set; }

    public long TransmittedFrameCount { get; private set; }

    public long ReceivedFrameCount { get; private set; }

    public long InvalidTransmittedFrameCount { get; private set; }

    internal long Cycles => _bus.Cycles;

    internal IDisposable ScheduleEvent(long cycle, Action<long> callback) =>
        _bus.SchedulePeripheralEvent(cycle, callback);

    /// <summary>
    /// Publishes one unescaped IrLAP frame without its two-byte physical FCS.
    /// The callback runs on the ARM modem owner.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? FrameTransmitted;

    /// <summary>
    /// Raised on the ARM modem owner when the native UART switches the SIR
    /// port on or off.
    /// </summary>
    public event Action<bool>? PortEnabledChanged;

    /// <summary>
    /// Delivers one IrLAP frame to the native modem stack through FIFO3 and
    /// the recovered 0x1000 receive interrupt.
    /// </summary>
    public void QueueReceivedFrame(ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Length < 2)
        {
            throw new ArgumentException(
                "An IrLAP frame must contain address and control bytes.",
                nameof(frame));
        }

        ushort fcs = InitialFcs;
        foreach (byte value in frame)
        {
            fcs = UpdateFcs(fcs, value);
        }
        fcs = (ushort)~fcs;

        var wire = new List<byte>(frame.Length + 8) { Bof };
        foreach (byte value in frame)
        {
            AppendEscaped(wire, value);
        }
        AppendEscaped(wire, (byte)fcs);
        AppendEscaped(wire, (byte)(fcs >> 8));
        wire.Add(Eof);
        _bus.QueueInfraredReceivedBytes(wire.ToArray());
        ReceivedFrameCount++;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _bus.InfraredByteTransmitted -= ObserveTransmittedByte;
        _bus.MmioAccessed -= ObserveMmio;
        _disposed = true;
    }

    void ObserveMmio(ArmModemMmioAccess access)
    {
        if (!IsInfraredControlWrite(access))
        {
            return;
        }
        // 0x04 selects SIR, while 0x0c selects the physically shared
        // UART/FIFO3 path for the bottom-connector LLRS232 cable.
        bool enabled = (access.Value & 0x0c) == 0x04;
        if (PortEnabled == enabled)
        {
            return;
        }
        PortEnabled = enabled;
        PortEnabledChanged?.Invoke(enabled);
    }

    static bool IsInfraredControlWrite(ArmModemMmioAccess access) =>
        access.IsWrite &&
        access.Address == 0x00800104 &&
        access.Size == 1;

    void ObserveTransmittedByte(byte value)
    {
        switch (value)
        {
            case Bof:
                StartTransmittedFrame();
                return;
            case Eof when _receiving:
                FinishTransmittedFrame();
                return;
            case Escape when _receiving && !_escaped:
                _escaped = true;
                return;
            default:
                AppendTransmittedByte(value);
                return;
        }
    }

    void StartTransmittedFrame()
    {
        _receiveBuffer.Clear();
        _receiving = true;
        _escaped = false;
    }

    void FinishTransmittedFrame()
    {
        CompleteTransmittedFrame();
        _receiveBuffer.Clear();
        _receiving = false;
        _escaped = false;
    }

    void AppendTransmittedByte(byte value)
    {
        if (!_receiving)
        {
            return;
        }

        if (_receiveBuffer.Count >= MaximumFrameLength)
        {
            _receiveBuffer.Clear();
            _receiving = false;
            _escaped = false;
            InvalidTransmittedFrameCount++;
            return;
        }

        _receiveBuffer.Add(_escaped ? (byte)(value ^ 0x20) : value);
        _escaped = false;
    }

    void CompleteTransmittedFrame()
    {
        if (_escaped || _receiveBuffer.Count < 4)
        {
            InvalidTransmittedFrameCount++;
            return;
        }

        ushort fcs = InitialFcs;
        foreach (byte value in _receiveBuffer)
        {
            fcs = UpdateFcs(fcs, value);
        }
        if (fcs != ValidFrameResidue)
        {
            InvalidTransmittedFrameCount++;
            return;
        }

        byte[] frame = _receiveBuffer
            .Take(_receiveBuffer.Count - sizeof(ushort))
            .ToArray();
        TransmittedFrameCount++;
        FrameTransmitted?.Invoke(frame);
    }

    static ushort UpdateFcs(ushort fcs, byte value)
    {
        fcs ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            fcs = (ushort)((fcs & 1) != 0
                ? (fcs >> 1) ^ 0x8408
                : fcs >> 1);
        }
        return fcs;
    }

    static void AppendEscaped(List<byte> target, byte value)
    {
        if (value is Bof or Eof or Escape)
        {
            target.Add(Escape);
            target.Add((byte)(value ^ 0x20));
        }
        else
        {
            target.Add(value);
        }
    }
}
