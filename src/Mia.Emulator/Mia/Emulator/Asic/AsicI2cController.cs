// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Interrupt-driven model of either ASIC I2C block. The firmware-visible
/// status values follow the standard Philips I2C states.
/// </summary>
internal sealed class AsicI2cController
{
    public const int DataAddress = 0x0859;
    public const int ControlAddress = 0x085a;
    public const int StatusAddress = 0x085b;
    public const int ConfigurationAddress = 0x085c;
    public const byte BootConfiguration = 0x04;
    public const byte TransmitDmaConfiguration = 0x86;
    public const int DmaSourceLowAddress = 0x0820;
    public const int DmaLengthLowAddress = 0x0822;
    public const int DmaControlAddress = 0x0824;
    public const byte DmaControlForward = 0xb6;
    public const byte DmaControlReverse = 0xf6;

    // The boot configuration selects an approximately 400 kHz bus from the
    // handset's 13 MHz reference (32 ASIC ticks per I2C clock). Address and data
    // completions occur after all nine wire clocks, including ACK, rather than
    // after a single I2C clock.
    public const int CompletionCycles = 9 * 32;
    internal const int DmaTransferCyclesPerByte = 1;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController _interruptController;
    readonly int _dataAddress;
    readonly int _controlAddress;
    readonly int _statusAddress;
    readonly int _configurationAddress;
    readonly byte _interruptSource;
    readonly Func<byte, bool> _addressAcknowledge;
    readonly Action<byte, byte[]>? _writeTransaction;
    readonly Func<byte, byte>? _readByte;
    readonly bool _supportsTransmitDma;
    readonly MiaWorker? _worker;
    readonly List<byte> _payload = [];
    byte _data;
    byte _control;
    byte _configuration;
    byte _address;
    byte _status;
    bool _dataPending;
    bool _started;
    bool _addressTransferred;
    bool _active;
    bool _reading;
    bool _dmaTransferred;

    public AsicI2cController(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        S4595Display display,
        MiaWorker? worker = null)
        : this(
            cpu,
            clock,
            interruptController,
            DataAddress,
            AsicInterruptController.I2c1Source,
            address => (address & 0xfe) == S4595Display.WriteAddress,
            (address, payload) => display.WriteTransaction(address, payload),
            readByte: null,
            supportsTransmitDma: true,
            worker: worker)
    {
    }

    public AsicI2cController(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        int dataAddress,
        byte interruptSource,
        Func<byte, bool>? addressAcknowledge = null,
        Action<byte, byte[]>? writeTransaction = null,
        Func<byte, byte>? readByte = null,
        bool supportsTransmitDma = false,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _dataAddress = dataAddress;
        _controlAddress = dataAddress + 1;
        _statusAddress = dataAddress + 2;
        _configurationAddress = dataAddress + 3;
        _interruptSource = interruptSource;
        _addressAcknowledge = addressAcknowledge ?? (_ => false);
        _writeTransaction = writeTransaction;
        _readByte = readByte;
        _supportsTransmitDma = supportsTransmitDma;
        _worker = worker;
        cpu.WriteHooks[_dataAddress] = WriteData;
        cpu.ReadHooks[_dataAddress] = _ => Volatile.Read(ref _data);
        cpu.WriteHooks[_controlAddress] = WriteControl;
        cpu.ReadHooks[_controlAddress] = _ => Volatile.Read(ref _control);
        cpu.ReadHooks[_statusAddress] = _ => Status;
        cpu.WriteHooks[_configurationAddress] = WriteConfiguration;
        cpu.ReadHooks[_configurationAddress] = _ =>
            Volatile.Read(ref _configuration);
    }

    public byte Status => Volatile.Read(ref _status);

    public long CompletedSteps { get; private set; }

    public long CompletedTransactions { get; private set; }

    public byte LastAddress { get; private set; }

    public long DmaTransferCount { get; private set; }

    public long DmaByteCount { get; private set; }

    public long RejectedDmaCount { get; private set; }

    public int LastDmaSource { get; private set; }

    public int LastDmaLength { get; private set; }

    public long LastDmaCycle { get; private set; }

    public int LastDmaPc { get; private set; }

    public long LastWriteCycle { get; private set; }

    public int LastWritePc { get; private set; }

    public event Action<byte, byte[]>? TransactionCompleted;

    bool WriteData(byte value, byte _, int __, byte ___)
    {
        Volatile.Write(ref _data, value);
        _dataPending = true;
        return false;
    }

    bool WriteControl(byte value, byte _, int __, byte ___)
    {
        Volatile.Write(ref _control, (byte)(value & ~0x10));
        switch (value)
        {
            case 0x80:
                ContinueTransfer();
                break;
            case 0x84:
                ReceiveByte(acknowledge: true);
                break;
            case 0xa0:
                StartTransfer();
                break;
            case 0x98:
                StopTransfer();
                break;
            case 0x00:
            case 0x90:
                // Initialization/recovery commands do not complete a byte.
                break;
        }
        return false;
    }

    bool WriteConfiguration(byte value, byte oldValue, int _, byte mask)
    {
        Volatile.Write(
            ref _configuration,
            (byte)((oldValue & ~mask) | (value & mask)));
        return false;
    }

    void StartTransfer()
    {
        var repeated = _started;
        if (repeated && _active && !_reading)
        {
            CompleteWritePhase();
        }
        _started = true;
        _addressTransferred = false;
        _active = false;
        _reading = false;
        _dataPending = false;
        _dmaTransferred = false;
        _payload.Clear();
        ScheduleCompletion(repeated ? (byte)0x10 : (byte)0x08);
    }

    void ContinueTransfer()
    {
        switch (GetTransferPhase())
        {
            case AsicI2cTransferPhase.NotStarted:
                ScheduleCompletion(0x00);
                break;
            case AsicI2cTransferPhase.Address:
                TransferAddress();
                break;
            case AsicI2cTransferPhase.Inactive:
                ScheduleCompletion(_reading ? (byte)0x48 : (byte)0x20);
                break;
            case AsicI2cTransferPhase.Read:
                ReceiveByte(acknowledge: false);
                break;
            case AsicI2cTransferPhase.Write:
                ContinueWriteTransfer();
                break;
        }
    }

    AsicI2cTransferPhase GetTransferPhase() =>
        (!_started, _addressTransferred, _active, _reading) switch
        {
            (true, _, _, _) => AsicI2cTransferPhase.NotStarted,
            (_, false, _, _) => AsicI2cTransferPhase.Address,
            (_, _, false, _) => AsicI2cTransferPhase.Inactive,
            (_, _, _, true) => AsicI2cTransferPhase.Read,
            _ => AsicI2cTransferPhase.Write,
        };

    void TransferAddress()
    {
        _address = _data;
        _dataPending = false;
        LastAddress = _address;
        _reading = (_address & 1) != 0;
        _payload.Clear();
        var acknowledged = _addressAcknowledge(_address);
        _addressTransferred = true;
        _active = acknowledged;
        ScheduleCompletion(acknowledged
            ? (_reading ? (byte)0x40 : (byte)0x18)
            : (_reading ? (byte)0x48 : (byte)0x20));
    }

    void ContinueWriteTransfer()
    {
        // The native driver also writes 0x80 after its transmit byte count has
        // reached zero, without writing the data register again. That advances
        // the controller into its terminal phase; it does not retransmit the
        // stale final byte still visible in the data register.
        if (_dataPending)
        {
            _payload.Add(_data);
            _dataPending = false;
        }
        else if (TryContinueTransmitDma())
        {
            return;
        }
        ScheduleCompletion(0x28);
    }

    bool TryContinueTransmitDma()
    {
        if (!_supportsTransmitDma || _dmaTransferred ||
            _configuration != TransmitDmaConfiguration ||
            _payload.Count != 1 || _payload[0] != S4595Display.ScanRowCommand)
        {
            return false;
        }

        _dmaTransferred = true;
        LastDmaCycle = _clock.Cycles;
        LastDmaPc = _cpu.PC;
        var control = _cpu.Data[DmaControlAddress];
        var sourceOffset = _cpu.Data[DmaSourceLowAddress] |
            (_cpu.Data[DmaSourceLowAddress + 1] << 8);
        var length = _cpu.Data[DmaLengthLowAddress] |
            (_cpu.Data[DmaLengthLowAddress + 1] << 8);
        var source = AsicCommandPort.GraphicsRamBase | sourceOffset;
        LastDmaSource = source;
        LastDmaLength = length;

        if (control is not (DmaControlForward or DmaControlReverse) ||
            length == 0 || source < 0 || (long)source + length > _cpu.Data.Length)
        {
            RejectedDmaCount++;
            ScheduleCompletion(0x00);
            return true;
        }

        for (var offset = 0; offset < length; offset++)
        {
            _payload.Add(_cpu.Data[source + offset]);
        }
        DmaTransferCount++;
        DmaByteCount += length;
        // The display DMA is a controller operation, not 8,080 CPU-driven
        // byte completions. Keep its firmware-visible completion asynchronous
        // without making the CPU wait on a host simulation of every wire bit.
        ScheduleCompletion(0x28, GetDmaCompletionCycles(length));
        return true;
    }

    internal static long GetDmaCompletionCycles(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        return Math.Max(
            CompletionCycles,
            (long)byteCount * DmaTransferCyclesPerByte);
    }

    void ReceiveByte(bool acknowledge)
    {
        if (!_active || !_reading)
        {
            ScheduleCompletion(0x00);
            return;
        }
        Volatile.Write(
            ref _data,
            _readByte?.Invoke(_address) ?? (byte)0xff);
        _cpu.Data[_dataAddress] = _data;
        ScheduleCompletion(acknowledge ? (byte)0x50 : (byte)0x58);
    }

    void StopTransfer()
    {
        if (_active && !_reading)
        {
            CompleteWritePhase();
        }
        if (_started)
        {
            CompletedTransactions++;
        }
        _started = false;
        _addressTransferred = false;
        _active = false;
        _reading = false;
        _dataPending = false;
        _dmaTransferred = false;
        _payload.Clear();
    }

    void CompleteWritePhase()
    {
        LastWriteCycle = _clock.Cycles;
        LastWritePc = _cpu.PC;
        var payload = _payload.ToArray();
        _writeTransaction?.Invoke(_address, payload);
        TransactionCompleted?.Invoke(_address, payload);
    }

    void ScheduleCompletion(byte status, long cycles = CompletionCycles)
    {
        Action complete = () =>
        {
            Volatile.Write(ref _status, status);
            Volatile.Write(ref _control, (byte)(_control | 0x10));
            _cpu.Data[_statusAddress] = status;
            CompletedSteps++;
            _interruptController.RaiseHighPriority(_interruptSource);
        };
        if (_worker is null)
        {
            _clock.Schedule(complete, cycles);
        }
        else
        {
            _clock.Schedule(_worker, complete, cycles);
        }
    }
}
