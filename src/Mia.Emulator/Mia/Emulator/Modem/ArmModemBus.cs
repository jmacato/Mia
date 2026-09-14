// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Arm7Core;

namespace Mia.Emulator.Modem;

internal sealed class ArmModemBus : IArm7Bus
{
    public const uint InternalRamSize = 0x00012000;
    public const uint ExternalRamBase = 0x01400000;
    public const uint ExternalRamSize = 0x00080000;
    public const uint ExternalRamMirrorSpan = 0x00100000;
    public const uint CpuCyclesPerTimerTick = 203;
    public const uint TimerInterruptBit = 0x00000008;
    public const int OseTimerTickMilliseconds = 5;
    public const int OseTimerInterruptPeriodCycles =
        MiaSystemClock.AsicCyclesPerSecond / 1000 * OseTimerTickMilliseconds;
    public const uint DspInterruptBit = 0x00010000;
    public const uint Uart1TransmitInterruptBit = 0x00000200;
    public const uint Uart1ReceiveInterruptBit = 0x00000400;
    public const uint InfraredTransmitInterruptBit = 0x00000800;
    public const uint InfraredReceiveInterruptBit = 0x00001000;
    public const uint OneShotInterruptBit = 0x00100000;
    public const uint NormalCableDetectInterruptBit = 0x08000000;
    public const int Uart1TransmitFifoCapacity = 0x80;
    public const int Uart1CharacterCycles = 282;
    internal const int MaximumUartReceiveBacklog = 4096;
    internal const int MaximumInfraredReceiveBacklog = 16384;

    readonly ArmModemFlash _flash;
    readonly byte[] _internalRam = new byte[InternalRamSize];
    readonly byte[] _externalRam = new byte[ExternalRamSize];
    readonly byte[] _bootScratch = new byte[0x24];
    readonly byte[] _asicTable = new byte[0x400];
    readonly ArmModemAddressRegion _internalRamRegion;
    readonly ArmModemAddressRegion _bootScratchRegion;
    readonly ArmModemAddressRegion _asicTableRegion;
    readonly Dictionary<(uint Address, int Size), Action<uint>> _mmioWriters;
    readonly ConcurrentQueue<byte> _uart1Received = new();
    readonly ConcurrentQueue<byte> _infraredReceived = new();
    readonly Queue<ArmModemBusScheduledUartTransmission> _uart1Transmit = new();
    readonly Queue<byte> _dspInboundTransfer = new();
    readonly byte[] _dspIndexedRegisters = new byte[0x100];
    readonly bool[] _dspIndexedRegisterValid = new bool[0x100];
    readonly List<byte> _dspPacketBytes = [];
    readonly List<byte> _dspTransferPayload = [];
    readonly PriorityQueue<ArmModemBusScheduledUartReceive, (long Cycle, long Sequence)>
        _scheduledUart1Receive = new();
    readonly PriorityQueue<ArmModemBusScheduledPeripheralEvent, (long Cycle, long Sequence)>
        _scheduledPeripheralEvents = new();
    readonly List<ArmModemBusExternalRamWriteObserver> _externalRamWriteObservers = [];
    readonly Dictionary<uint, byte> _byteRegisters = new()
    {
        [0x00800004] = 0,
        [0x0080000c] = 0,
        [0x00800110] = 0,
        [0x00800104] = 0,
        [0x0080010c] = 0,
        [0x00800204] = 0,
        [0x00800208] = 0,
        [0x0080020c] = 0,
        [0x00800708] = 0,
        [0x00800710] = 0,
        [0x00800718] = 0,
        [0x00800908] = 0,
        [0x00800910] = 0,
        [0x00800914] = 0,
        [0x00800920] = 0,
        [0x00800924] = 0,
        [0x00800930] = 0,
        [0x00800934] = 0,
        [0x00800954] = 0,
        [0x00800958] = 0,
        [0x0080095c] = 0,
        [0x00800960] = 0,
        [0x00800b04] = 0,
        [0x00800b08] = 0,
        [0x00800b14] = 0,
        [0x00800b18] = 0,
        [0x00800b24] = 0,
        [0x00800b28] = 0,
        [0x00800b34] = 0,
        [0x00800b38] = 0,
        [0x00800d00] = 0,
        [0x00800d08] = 0,
        [0x00800d0c] = 0,
        [0x00800d10] = 0,
        [0x00800d14] = 0,
        [0x00800d18] = 0,
        [0x00800d20] = 0,
        [0x00800d24] = 0,
        [0x00800d28] = 0,
        [0x00800f0c] = 0,
        [0x00800e04] = 0,
        [0x00800e08] = 0,
        [0x00800e0c] = 0,
        [0x00800e1c] = 0,
        [0x00800410] = 0,
    };
    readonly Dictionary<uint, byte> _readOnlyByteRegisters = new()
    {
        // UART1 follows the 16550 register layout. With no pending interrupt
        // or line error, IIR reports 1 and LSR reports transmitter idle.
        [0x00800000] = 0,
        [0x00800008] = 1,
        [0x00800014] = 0x60,
        [0x00800018] = 0,
        // The IrDA SIR path uses UART2 plus the ASIC FIFO3 byte ports. The
        // line starts idle; FIFO occupancy is provided dynamically below.
        [0x00800100] = 0,
        [0x00800108] = 1,
        [0x00800114] = 0x60,
        [0x00800118] = 0,
        [0x00800214] = 0x60,
        [0x00800218] = 0,
        [0x00800400] = 0,
        [0x00800b2c] = 0,
    };
    readonly Dictionary<uint, ushort> _halfRegisters = new()
    {
        [0x00800404] = 0,
        [0x00800408] = 0,
        [0x0080071c] = 0,
        [0x0080090c] = 0,
        [0x00800918] = 0,
        [0x0080091c] = 0,
        [0x00800928] = 0,
        [0x0080092c] = 0,
        [0x00800938] = 0,
        [0x00800d04] = 0,
    };
    byte _bootStatus;
    byte _linkManagerInterruptStatus;
    byte _dspPacketPortStatusInputs;
    byte _dspSecondaryPacketPortStatusInputs;
    byte _dspIndexedAddress;
    byte _dspTransferControl;
    byte _dspTransferKind;
    int _dspTransferLength = -1;
    bool _dspTransferActive;
    byte _powerMode;
    volatile bool _uart1ReceiveEnabled;
    uint _irqDisableMask = uint.MaxValue;
    uint _irqPending;
    bool _irqLineAsserted;
    bool _infraredTransmitReady;
    uint _highPriorityDisableMask;
    long _nextTimerInterruptCycle = long.MaxValue;
    long _nextUart1TransmitCycle = long.MaxValue;
    long _nextUart1ReceiveCycle = long.MaxValue;
    long _nextExternalDspFrameCycle = long.MaxValue;
    long _nextPeripheralEventCycle = long.MaxValue;
    long _uart1TransmitTailCycle;
    long _uart1ReceiveSequence;
    long _peripheralEventSequence;
    int _cancelledPeripheralEventCount;
    long _nextBusEventCycle = long.MaxValue;
    long _timerInterruptPeriodCycles;
    long _timerBaseCycle;
    long _oneShotGeneration;
    IDisposable? _oneShotEvent;
    uint _timerBaseValue;
    uint _watchdogControl;

    public ArmModemBus(ReadOnlySpan<byte> firmware, ArmModemFlashProfile flashProfile)
    {
        _flash = new ArmModemFlash(firmware, flashProfile);
        _internalRamRegion = new(0, InternalRamSize, _internalRam);
        _bootScratchRegion = new(
            0x00800300,
            checked((uint)_bootScratch.Length),
            _bootScratch);
        _asicTableRegion = new(
            0x00801000,
            checked((uint)_asicTable.Length),
            _asicTable);
        _mmioWriters = new()
        {
            [(0x0080001c, 1)] = value => _bootStatus = (byte)value,
            [(0x00800508, 4)] = DisableInterrupts,
            [(0x0080050c, 4)] = EnableInterrupts,
            [(0x00800608, 4)] = value => _highPriorityDisableMask |= value,
            [(0x0080060c, 4)] = value => _highPriorityDisableMask &= ~value,
            [(0x00800700, 4)] = SetTimerCounter,
            [(0x00800800, 1)] = value => AcknowledgeDspInterrupt((byte)value),
            [(0x00800808, 1)] = value => _dspIndexedAddress = (byte)value,
            [(0x00800804, 1)] = value => ObserveDspPacketByte((byte)value),
            [(0x0080080c, 1)] = value => ObserveDspTransferControl((byte)value),
            [(0x00800810, 1)] = value => ObserveDspTransferData((byte)value),
            [(0x00800900, 1)] = SetPowerMode,
            [(0x00800950, 1)] = static _ => { },
            [(0x00800b00, 1)] = TransmitUart1Byte,
            [(0x00800200, 1)] = value => TransmitUart2Byte((byte)value),
            [(0x00800b20, 1)] = value => TransmitSharedSerialByte((byte)value),
            [(0x00800930, 1)] = value => SetOneShotControl((byte)value),
            [(0x00800954, 1)] = value => SetCableDetectControl((byte)value),
            [(0x00800b34, 1)] = value => SetInfraredReceiveControl((byte)value),
            [(0x00800b14, 1)] = value => SetUart1ReceiveControl((byte)value),
            [(0x00800c0c, 4)] = value => _watchdogControl = value,
        };
    }

    public long Cycles { get; private set; }

    public long NextEventCycle => _nextBusEventCycle;

    public uint CurrentPc { get; set; }

    internal Func<uint>? CurrentPcProvider { get; set; }

    public ArmModemFlash Flash => _flash;

    public byte PowerMode => _powerMode;

    public bool SleepRequested { get; private set; }

    public bool IrqLineAsserted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _irqLineAsserted;
    }

    public uint IrqPending => GetPendingInterrupts();

    public bool NormalCableConnected =>
        (_readOnlyByteRegisters[0x00800400] & 0x80) != 0;

    public bool ExternalSerialPortEnabled =>
        (_byteRegisters[0x00800104] & 0x0c) == 0x0c;

    /// <summary>
    /// Current firmware-visible DSP interrupt status at 0x00800800.
    /// Multiple independent causes may be asserted at once.
    /// </summary>
    public byte DspInterruptStatus => _linkManagerInterruptStatus;

    public long TimerInterruptPeriodCycles => _timerInterruptPeriodCycles;

    public long TimerInterruptCount { get; private set; }

    public bool Uart1ReceiveEnabled => _uart1ReceiveEnabled;

    public int Uart1ReceivedCount => _uart1Received.Count;

    public int Uart1TransmitQueuedCount => _uart1Transmit.Count;

    /// <summary>
    /// Number of raw bytes awaiting consumption on the DSP-to-ARM secondary
    /// transfer FIFO. A frame source must not overwrite an unconsumed frame.
    /// </summary>
    public int DspInboundTransferByteCount => _dspInboundTransfer.Count;

    public long Uart1DroppedReceiveCount { get; private set; }

    public long Uart1TransmitCount { get; private set; }

    public long Uart2TransmitCount { get; private set; }

    public long InfraredTransmitCount { get; private set; }

    public event Action<byte>? Uart1ByteTransmitted;

    /// <summary>
    /// Raised when a byte enters the serializer, before its completion. The
    /// cycle is in the modem's 13 MHz domain.
    /// </summary>
    public event Action<byte, long>? Uart1TransmissionScheduled;

    public event Action<byte>? Uart1ByteReceived;

    public event Action<byte>? Uart1ByteRead;

    public event Action<byte>? Uart2ByteTransmitted;

    /// <summary>
    /// Raised for each SIR wire byte written by the native LLIrDA driver to
    /// ASIC FIFO3 at <c>0x00800b20</c>.
    /// </summary>
    public event Action<byte>? InfraredByteTransmitted;

    /// <summary>
    /// Raised for each wired LLRS232 byte written through ASIC FIFO3 while
    /// UART2 is in the normal-cable configuration recovered from firmware.
    /// </summary>
    public event Action<byte>? ExternalSerialByteTransmitted;

    public event Action<ArmModemMmioAccess>? MmioAccessed;

    /// <summary>
    /// Observes writes to external ARM RAM. This is diagnostic-only and is
    /// raised after the native store has completed.
    /// </summary>
    public event Action<ArmModemRamWrite>? ExternalRamWritten;

    public event Action<ArmModemDspPacket>? DspPacketTransmitted;

    public event Action<ArmModemDspTransfer>? DspTransferTransmitted;

    /// <summary>
    /// Raised when the firmware-visible DSP interrupt status changes.
    /// Peripherals use this edge to resume work blocked behind an earlier
    /// level-sensitive DSP event.
    /// </summary>
    public event Action<byte>? DspInterruptStatusChanged;

    /// <summary>
    /// Raised at a DSP frame boundary scheduled by a firmware-visible modem
    /// peripheral. The callback runs before the firmware can idle past it.
    /// </summary>
    public event Action<long>? ExternalDspFrameDue;

    /// <summary>
    /// Schedules one DSP frame boundary. A peripheral schedules its next frame
    /// from this callback, so only one boundary is outstanding at a time.
    /// </summary>
    public void ScheduleExternalDspFrame(long cycle)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, Cycles);

        _nextExternalDspFrameCycle = cycle;
        UpdateNextBusEventCycle();
    }

    /// <summary>
    /// Schedules peripheral work in the modem cycle domain. Bus dispatch also
    /// runs while the ARM sleeps, so no host-batch or instruction polling is
    /// needed to make transport progress.
    /// </summary>
    internal IDisposable SchedulePeripheralEvent(
        long cycle,
        Action<long> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, Cycles);

        long sequence = _peripheralEventSequence++;
        var scheduled = new ArmModemBusScheduledPeripheralEvent(
            callback,
            OnPeripheralEventCancelled,
            cycle,
            sequence);
        _scheduledPeripheralEvents.Enqueue(
            scheduled,
            (cycle, sequence));
        _nextPeripheralEventCycle = Math.Min(
            _nextPeripheralEventCycle,
            cycle);
        UpdateNextBusEventCycle();
        if (cycle == Cycles)
        {
            AdvanceBusEvents();
        }
        return scheduled;
    }

    /// <summary>
    /// Observes writes to one firmware-owned shared-RAM field without routing
    /// every external RAM write through the diagnostic event.
    /// </summary>
    internal IDisposable ObserveExternalRamWrite(
        uint address,
        Action<ArmModemRamWrite> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var observer = new ArmModemBusExternalRamWriteObserver(
            address,
            callback,
            RemoveExternalRamWriteObserver);
        _externalRamWriteObservers.Add(observer);
        return observer;
    }

    void RemoveExternalRamWriteObserver(ArmModemBusExternalRamWriteObserver observer) =>
        _externalRamWriteObservers.Remove(observer);

    void OnPeripheralEventCancelled()
    {
        _cancelledPeripheralEventCount++;
        DiscardCancelledPeripheralEvents();
        // A later cancelled event can sit behind a live timer indefinitely.
        // Compact occasionally so repeated rescheduling releases its callback
        // and captured buffers even when that timer has not fired yet.
        if (_cancelledPeripheralEventCount >= 32)
        {
            var live = _scheduledPeripheralEvents.UnorderedItems
                .Where(item => !item.Element.IsCancelled).ToArray();
            _scheduledPeripheralEvents.Clear();
            _scheduledPeripheralEvents.EnqueueRange(live);
            _cancelledPeripheralEventCount = 0;
        }
        _nextPeripheralEventCycle = _scheduledPeripheralEvents.TryPeek(
            out _,
            out var nextPriority)
            ? nextPriority.Cycle
            : long.MaxValue;
        UpdateNextBusEventCycle();
    }

    public byte[] SnapshotInternal(uint address, int length) =>
        _internalRam.AsSpan((int)address, length).ToArray();

    public byte[] SnapshotExternal(uint address, int length)
    {
        if (address < ExternalRamBase ||
            address - ExternalRamBase + length > ExternalRamMirrorSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        int offset = (int)((address - ExternalRamBase) & (ExternalRamSize - 1));
        if (offset + length > ExternalRamSize)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        return _externalRam.AsSpan(offset, length).ToArray();
    }

    /// <summary>
    /// Applies a write from a bus-attached peripheral to shared external RAM.
    /// The ARM firmware uses this RAM for controller/DSP descriptors, so this
    /// is distinct from an ARM CPU store and has no CPU-side MMIO effect.
    /// </summary>
    public void WritePeripheralSharedExternal(uint address, ReadOnlySpan<byte> bytes)
    {
        if (address < ExternalRamBase ||
            address - ExternalRamBase + bytes.Length > ExternalRamMirrorSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        int offset = (int)((address - ExternalRamBase) & (ExternalRamSize - 1));
        if (offset + bytes.Length > ExternalRamSize)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }
        bytes.CopyTo(_externalRam.AsSpan(offset, bytes.Length));
    }

    public void QueueDspInboundPayload(ReadOnlySpan<byte> payload)
        => QueueDspInboundTransfer(0, 3, payload);

    public void QueueDspInboundTransfer(
        byte control,
        byte kind,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0x1f)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }
        if (kind > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _dspInboundTransfer.Enqueue(control);
        _dspInboundTransfer.Enqueue((byte)((payload.Length << 3) | kind));
        foreach (byte value in payload)
        {
            _dspInboundTransfer.Enqueue(value);
        }

        AssertDspStatus(0x10);
    }

    public void SetDspIndexedRegister(byte address, byte value)
    {
        _dspIndexedRegisters[address] = value;
        _dspIndexedRegisterValid[address] = true;
    }

    public void SetDspIndexedReadAddress(byte address)
    {
        _dspIndexedAddress = address;
    }

    public void AssertDspStatus(byte value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        byte previousStatus = _linkManagerInterruptStatus;
        _linkManagerInterruptStatus |= value;
        _irqPending |= DspInterruptBit;
        UpdateIrqLine();
        if (_linkManagerInterruptStatus != previousStatus)
        {
            DspInterruptStatusChanged?.Invoke(_linkManagerInterruptStatus);
        }
    }

    /// <summary>
    /// Drives level-sensitive status inputs for the DSP packet port at
    /// <c>0x0080000c</c>. These inputs are neither interrupt causes nor
    /// acknowledgeable through the link-manager status register.
    /// </summary>
    public void SetDspPacketPortStatusInput(byte mask, bool asserted)
    {
        if (asserted)
        {
            _dspPacketPortStatusInputs |= mask;
        }
        else
        {
            _dspPacketPortStatusInputs &= (byte)~mask;
        }
    }

    /// <summary>
    /// Drives level-sensitive status inputs for the paired DSP packet port at
    /// <c>0x00800c00</c>. This port uses the same ready-bit convention as
    /// <c>0x0080000c</c> but is separately addressed by the receiver's
    /// alternate scheduling branch.
    /// </summary>
    public void SetDspSecondaryPacketPortStatusInput(byte mask, bool asserted)
    {
        if (asserted)
        {
            _dspSecondaryPacketPortStatusInputs |= mask;
        }
        else
        {
            _dspSecondaryPacketPortStatusInputs &= (byte)~mask;
        }
    }

    public void QueueUart1ReceivedByte(byte value)
    {
        // Every caller marshals onto the modem's owning worker before
        // reaching here (ArmModemLinkBridge's explicit Invoke, or the
        // ARM-execution-driven advance/MMIO paths below), so this needs no
        // synchronization beyond the already-volatile enable flag and
        // already-concurrent receive queue.
        if (!_uart1ReceiveEnabled || _uart1Received.Count >= MaximumUartReceiveBacklog)
        {
            Uart1DroppedReceiveCount++;
            return;
        }

        _uart1Received.Enqueue(value);
        UpdateIrqLine();
        Uart1ByteReceived?.Invoke(value);
    }

    /// <summary>
    /// Queues one SIR wire byte for the native LLIrDA receive state machine.
    /// FIFO3 exposes occupancy at <c>0x00800b3c</c>, data at
    /// <c>0x00800b30</c>, and raises interrupt source 0x1000 while nonempty.
    /// </summary>
    public void QueueInfraredReceivedByte(byte value)
    {
        if (_infraredReceived.Count < MaximumInfraredReceiveBacklog)
            _infraredReceived.Enqueue(value);
        UpdateIrqLine();
    }

    public void QueueInfraredReceivedBytes(ReadOnlySpan<byte> values)
    {
        // Accept complete peer frames or none of them on receive overrun.
        if (values.Length > MaximumInfraredReceiveBacklog - _infraredReceived.Count) return;
        foreach (byte value in values)
        {
            _infraredReceived.Enqueue(value);
        }
        UpdateIrqLine();
    }

    /// <summary>
    /// Queues bytes at the shared FIFO3 receive boundary while UART2 is in
    /// wired LLRS232 mode.
    /// </summary>
    public void QueueExternalSerialReceivedBytes(ReadOnlySpan<byte> values)
    {
        if (values.Length > MaximumInfraredReceiveBacklog - _infraredReceived.Count) return;
        foreach (byte value in values)
        {
            _infraredReceived.Enqueue(value);
        }
        UpdateIrqLine();
    }

    public void ClearExternalSerialReceivedBytes()
    {
        _infraredReceived.Clear();
        UpdateIrqLine();
    }

    /// <summary>
    /// Drives the normal-cable GPIO observed by the native detector and raises
    /// its edge interrupt on attach and detach.
    /// </summary>
    public void SetNormalCableConnected(bool connected)
    {
        if (NormalCableConnected == connected)
        {
            return;
        }

        byte gpio = _readOnlyByteRegisters[0x00800400];
        _readOnlyByteRegisters[0x00800400] = connected
            ? (byte)(gpio | 0x80)
            : (byte)(gpio & ~0x80);
        _irqPending |= NormalCableDetectInterruptBit;
        UpdateIrqLine();
    }

    public void ScheduleUart1ReceivedByte(byte value, long arrivalCycle)
    {
        if (arrivalCycle < Cycles)
        {
            throw new InvalidOperationException(
                $"UART1 receive cycle {arrivalCycle:n0} is behind modem cycle {Cycles:n0}.");
        }

        long sequence = _uart1ReceiveSequence++;
        _scheduledUart1Receive.Enqueue(
            new(value, arrivalCycle),
            (arrivalCycle, sequence));
        _nextUart1ReceiveCycle = Math.Min(
            _nextUart1ReceiveCycle,
            arrivalCycle);
        UpdateNextBusEventCycle();
        if (arrivalCycle == Cycles)
        {
            AdvanceBusEvents();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadWord(uint address, ArmAccess access)
    {
        address &= ~3u;
        if ((access & ArmAccess.Code) == 0)
        {
            return Read(address, 4, access);
        }
        return ReadCodeWord(address, access);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint ReadCodeWord(uint address, ArmAccess access)
    {
        AdvanceCycle();
        if (address < InternalRamSize)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(
                _internalRam.AsSpan((int)address, sizeof(uint)));
        }
        uint ramOffset = address - ExternalRamBase;
        if (ramOffset < ExternalRamMirrorSpan)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(
                _externalRam.AsSpan((int)(ramOffset & (ExternalRamSize - 1)), sizeof(uint)));
        }
        if (_flash.TryReadCodeWord(address, out uint value))
        {
            return value;
        }
        return ReadAfterCycle(address, 4, access);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadHalf(uint address, ArmAccess access)
    {
        address &= ~1u;
        if ((access & ArmAccess.Code) == 0)
        {
            return Read(address, sizeof(ushort), access);
        }
        return ReadCodeHalf(address, access);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint ReadCodeHalf(uint address, ArmAccess access)
    {
        AdvanceCycle();
        if (address < InternalRamSize)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(
                _internalRam.AsSpan((int)address, sizeof(ushort)));
        }
        uint ramOffset = address - ExternalRamBase;
        if (ramOffset < ExternalRamMirrorSpan)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(
                _externalRam.AsSpan((int)(ramOffset & (ExternalRamSize - 1)), sizeof(ushort)));
        }
        if (_flash.TryReadCodeHalf(address, out uint value))
        {
            return value;
        }
        return ReadAfterCycle(address, sizeof(ushort), access);
    }

    public uint ReadByte(uint address, ArmAccess access) => Read(address, 1, access);

    public void WriteWord(uint address, uint value, ArmAccess access) =>
        Write(address & ~3u, value, 4);

    public void WriteHalf(uint address, ushort value, ArmAccess access) =>
        Write(address & ~1u, value, 2);

    public void WriteByte(uint address, byte value, ArmAccess access) =>
        Write(address, value, 1);

    public void Idle() => AdvanceCycle();

    public void IdleUntilCycle(long cycleLimit)
    {
        while (Cycles < cycleLimit && !IrqLineAsserted)
        {
            long nextEvent = _nextBusEventCycle;
            if (nextEvent > cycleLimit)
            {
                Cycles = cycleLimit;
                return;
            }

            Cycles = nextEvent;
            AdvanceBusEvents();
        }
    }

    public void WakeFromInterrupt() => SleepRequested = false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint Read(uint address, int size, ArmAccess access)
    {
        AdvanceCycle();
        // RAM accesses need no nullable region lookup or MMIO dispatch.
        if (address < InternalRamSize && size <= InternalRamSize - address)
            return ReadLittleEndian(_internalRam, (int)address, size);
        uint offset = address - ExternalRamBase;
        if (offset < ExternalRamMirrorSpan && size <= ExternalRamMirrorSpan - offset)
            return ReadLittleEndian(_externalRam, (int)(offset & (ExternalRamSize - 1)), size);
        if (size == 4 && _flash.TryReadCodeWord(address, out uint word))
            return word;
        return ReadAfterCycle(address, size, access);
    }

    uint ReadAfterCycle(uint address, int size, ArmAccess access)
    {
        uint value = ReadAfterCycleCore(address, size, access);
        ObserveMmio(address, size, isWrite: false, value);
        return value;
    }

    uint ReadAfterCycleCore(uint address, int size, ArmAccess access)
    {
        ThrowIfRomCall(address, access);
        return ReadMappedMemory(address, size) ??
            ReadMmio(address, size) ??
            throw ArmModemBusAccessException.Read(GetCurrentPc(), address, size);
    }

    static void ThrowIfRomCall(uint address, ArmAccess access)
    {
        if ((access & ArmAccess.Code) != 0 &&
            address is >= 0x00c00000 and < 0x00d00000)
        {
            throw new ArmModemRomCallException(address);
        }
    }

    uint? ReadMappedMemory(uint address, int size)
    {
        if (Resolve(address, size) is { } region)
        {
            return ReadLittleEndian(region.Backing, region.Offset, size);
        }

        if (size == 4 && _flash.TryReadCodeWord(address, out uint codeWord))
        {
            return codeWord;
        }

        return _flash.TryRead(address, size, out uint value)
            ? value
            : null;
    }

    uint? ReadMmio(uint address, int size) => (address, size) switch
    {
        (0x0080001c, 1) => _bootStatus,
        (0x0080000c, 1) =>
            (byte)(_byteRegisters.GetValueOrDefault(address) |
                _dspPacketPortStatusInputs),
        (0x00800c00, 1) =>
            (byte)(_byteRegisters.GetValueOrDefault(address) |
                _dspSecondaryPacketPortStatusInputs),
        (0x00800500, 4) => GetServiceableInterrupts(),
        (0x00800504, 4) => GetPendingInterrupts(),
        (0x00800508, 4) => _irqDisableMask,
        (0x00800608, 4) => _highPriorityDisableMask,
        (0x00800700, 4) => ReadTimerCounter(),
        (0x00800710, 1) => ReadAndAcknowledgeTimerPeriod(),
        (0x00800800, 1) => _linkManagerInterruptStatus,
        (0x00800808, 1) => ReadDspIndexedRegister(address, size),
        (0x00800810, 1) => ReadDspInboundTransfer(address, size),
        (0x00800900, 1) => _powerMode,
        (0x00800b0c, 1) => (byte)_uart1Transmit.Count,
        (0x00800b10, 1) => ReadUart1Receive(),
        (0x00800b1c, 1) =>
            (byte)Math.Min(_uart1Received.Count, byte.MaxValue),
        (0x00800b30, 1) => ReadInfraredReceive(),
        (0x00800b3c, 1) =>
            (byte)Math.Min(_infraredReceived.Count, byte.MaxValue),
        (0x00800c0c, 4) => _watchdogControl,
        (0x00c00004, 4) => 6,
        (_, 1) => ReadByteRegister(address),
        (_, 2) => ReadHalfRegister(address),
        _ => null,
    };

    uint ReadTimerCounter() =>
        (_timerBaseValue +
            (uint)((Cycles - _timerBaseCycle) / CpuCyclesPerTimerTick)) &
        0x00ffffff;

    internal bool TryGetStableTimerPoll(out uint counter, out long nextChangeCycle)
    {
        counter = ReadTimerCounter();
        nextChangeCycle = Math.Min(
            NextEventCycle,
            _timerBaseCycle +
                ((Cycles - _timerBaseCycle) / CpuCyclesPerTimerTick + 1) *
                CpuCyclesPerTimerTick);
        return MmioAccessed is null && (_byteRegisters[0x0080095c] & 0x10) == 0;
    }

    uint ReadAndAcknowledgeTimerPeriod()
    {
        byte value = _byteRegisters[0x00800710];
        _irqPending &= ~TimerInterruptBit;
        UpdateIrqLine();
        return value;
    }

    uint ReadDspIndexedRegister(uint address, int size)
    {
        byte addressToRead = _dspIndexedAddress++;
        if (!_dspIndexedRegisterValid[addressToRead])
        {
            throw ArmModemBusAccessException.Read(GetCurrentPc(), address, size);
        }
        return _dspIndexedRegisters[addressToRead];
    }

    uint ReadDspInboundTransfer(uint address, int size)
    {
        if (!_dspInboundTransfer.TryDequeue(out byte value))
        {
            throw ArmModemBusAccessException.Read(GetCurrentPc(), address, size);
        }
        return value;
    }

    uint ReadUart1Receive()
    {
        if (!_uart1Received.TryDequeue(out byte value))
        {
            return 0;
        }

        Uart1ByteRead?.Invoke(value);
        UpdateIrqLine();
        return value;
    }

    uint ReadInfraredReceive()
    {
        if (!_infraredReceived.TryDequeue(out byte value))
        {
            return 0;
        }

        UpdateIrqLine();
        return value;
    }

    uint? ReadByteRegister(uint address)
    {
        if (_byteRegisters.TryGetValue(address, out byte value))
        {
            return value;
        }
        return _readOnlyByteRegisters.TryGetValue(address, out value)
            ? value
            : null;
    }

    uint? ReadHalfRegister(uint address) =>
        _halfRegisters.TryGetValue(address, out ushort value)
            ? value
            : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Write(uint address, uint value, int size)
    {
        AdvanceCycle();
        if (address < InternalRamSize && size <= InternalRamSize - address)
        {
            WriteLittleEndian(_internalRam, (int)address, value, size);
            return;
        }
        uint offset = address - ExternalRamBase;
        if (offset < ExternalRamMirrorSpan && size <= ExternalRamMirrorSpan - offset)
        {
            WriteLittleEndian(_externalRam, (int)(offset & (ExternalRamSize - 1)), value, size);
            ObserveExternalRamWrite(address, value, size);
            return;
        }
        WriteAfterCycle(address, value, size);
        ObserveMmio(address, size, isWrite: true, value);
    }

    void WriteAfterCycle(uint address, uint value, int size)
    {
        if (TryWriteMappedMemory(address, value, size) ||
            _flash.TryWrite(address, value, size) ||
            TryWriteMmio(address, value, size) ||
            TryWriteStoredRegister(address, value, size))
        {
            return;
        }

        throw ArmModemBusAccessException.Write(GetCurrentPc(), address, value, size);
    }

    bool TryWriteMmio(uint address, uint value, int size)
    {
        if (!_mmioWriters.TryGetValue((address, size), out Action<uint>? write))
        {
            return false;
        }

        write(value);
        return true;
    }

    bool TryWriteMappedMemory(uint address, uint value, int size)
    {
        if (Resolve(address, size) is not { } region)
        {
            return false;
        }

        WriteLittleEndian(region.Backing, region.Offset, value, size);
        ObserveExternalRamWrite(address, value, size);
        return true;
    }

    void ObserveExternalRamWrite(uint address, uint value, int size)
    {
        if (address < ExternalRamBase ||
            address - ExternalRamBase + size > ExternalRamMirrorSpan)
        {
            return;
        }

        var write = new ArmModemRamWrite(
            Cycles,
            GetCurrentPc(),
            address,
            size,
            value);
        DispatchExternalRamWriteObservers(write);
        ExternalRamWritten?.Invoke(write);
    }

    void DisableInterrupts(uint value)
    {
        _irqDisableMask |= value;
        UpdateIrqLine();
    }

    void EnableInterrupts(uint value)
    {
        _irqDisableMask &= ~value;
        if ((value & InfraredTransmitInterruptBit) != 0)
        {
            _infraredTransmitReady = true;
        }
        UpdateIrqLine();
    }

    void SetTimerCounter(uint value)
    {
        _timerBaseValue = value & 0x00ffffff;
        _timerBaseCycle = Cycles;
    }

    void AcknowledgeDspInterrupt(byte value)
    {
        // This is a status-read/control-write register, not W1C storage.
        // The low bits of the read value encode a DSP event selector,
        // while the firmware writes its next control/acknowledgement mask.
        byte previousStatus = _linkManagerInterruptStatus;
        _linkManagerInterruptStatus =
            (value & 0x10) != 0 && _dspInboundTransfer.Count != 0
                ? (byte)0x10
                : (byte)0;
        if (_linkManagerInterruptStatus == 0)
        {
            _irqPending &= ~DspInterruptBit;
        }
        UpdateIrqLine();
        PublishDspInterruptStatusChange(previousStatus);
    }

    void PublishDspInterruptStatusChange(byte previousStatus)
    {
        if (_linkManagerInterruptStatus != previousStatus)
        {
            DspInterruptStatusChanged?.Invoke(_linkManagerInterruptStatus);
        }
    }

    void SetPowerMode(uint value)
    {
        if (value is not (0x07 or 0x0b or 0x13))
        {
            throw ArmModemBusAccessException.Write(
                GetCurrentPc(),
                0x00800900,
                value,
                1);
        }

        _powerMode = (byte)value;
        SleepRequested = value == 0x07;
    }

    void TransmitUart1Byte(uint value)
    {
        if (_uart1Transmit.Count >= Uart1TransmitFifoCapacity)
        {
            throw ArmModemBusAccessException.Write(
                GetCurrentPc(),
                0x00800b00,
                value,
                1);
        }

        long completionCycle = Math.Max(Cycles, _uart1TransmitTailCycle) +
            Uart1CharacterCycles;
        _uart1TransmitTailCycle = completionCycle;
        _uart1Transmit.Enqueue(new((byte)value, completionCycle));
        Uart1TransmissionScheduled?.Invoke((byte)value, completionCycle);
        Uart1TransmitCount++;
        StartUart1TransmitterIfIdle(completionCycle);
        UpdateIrqLine();
    }

    void StartUart1TransmitterIfIdle(long completionCycle)
    {
        if (_nextUart1TransmitCycle == long.MaxValue)
        {
            _nextUart1TransmitCycle = completionCycle;
            UpdateNextBusEventCycle();
        }
    }

    void TransmitUart2Byte(byte value)
    {
        Uart2TransmitCount++;
        Uart2ByteTransmitted?.Invoke(value);
    }

    void TransmitSharedSerialByte(byte value)
    {
        if (ExternalSerialPortEnabled)
        {
            ExternalSerialByteTransmitted?.Invoke(value);
            return;
        }

        InfraredTransmitCount++;
        InfraredByteTransmitted?.Invoke(value);
    }

    void SetOneShotControl(byte value)
    {
        _byteRegisters[0x00800930] = value;
        if (value == 0)
        {
            _oneShotGeneration++;
            _oneShotEvent?.Dispose();
            _oneShotEvent = null;
            _irqPending &= ~OneShotInterruptBit;
            UpdateIrqLine();
        }
    }

    void SetCableDetectControl(byte value)
    {
        _byteRegisters[0x00800954] = value;
        if (value == 0)
        {
            _irqPending &= ~NormalCableDetectInterruptBit;
            UpdateIrqLine();
        }
    }

    void SetInfraredReceiveControl(byte value)
    {
        _byteRegisters[0x00800b34] = value;
        if (value == 1)
        {
            _infraredReceived.Clear();
        }
        UpdateIrqLine();
    }

    void SetUart1ReceiveControl(byte value)
    {
        _byteRegisters[0x00800b14] = value;
        _uart1ReceiveEnabled = value != 0;
        if ((value & 1) != 0)
        {
            _uart1Received.Clear();
        }
        UpdateIrqLine();
    }

    bool TryWriteStoredRegister(uint address, uint value, int size) => size switch
    {
        1 => TryWriteByteRegister(address, (byte)value),
        2 => TryWriteHalfRegister(address, (ushort)value),
        _ => false,
    };

    bool TryWriteByteRegister(uint address, byte value)
    {
        if (!_byteRegisters.ContainsKey(address))
        {
            return false;
        }

        _byteRegisters[address] = value;
        if (address is 0x00800708 or 0x00800710 or 0x00800718 or
            0x00800910 or 0x00800914)
        {
            ReconfigureTimerInterrupt();
        }
        return true;
    }

    bool TryWriteHalfRegister(uint address, ushort value)
    {
        if (!_halfRegisters.ContainsKey(address))
        {
            return false;
        }

        _halfRegisters[address] = value;
        if (address == 0x0080071c)
        {
            ScheduleOneShot(value);
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AdvanceCycle()
    {
        Cycles++;
        if (Cycles >= _nextBusEventCycle)
        {
            AdvanceBusEvents();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void AdvanceBusEvents()
    {
        AdvanceUart1TransmitterIfDue();
        AdvanceUart1ReceiverIfDue();
        AdvanceTimerInterruptIfDue();
        AdvanceExternalDspFrameIfDue();
        AdvancePeripheralEventsIfDue();
        UpdateNextBusEventCycle();
    }

    void AdvanceUart1TransmitterIfDue()
    {
        if (Cycles >= _nextUart1TransmitCycle)
        {
            AdvanceUart1Transmitter();
        }
    }

    void AdvanceUart1ReceiverIfDue()
    {
        if (Cycles >= _nextUart1ReceiveCycle)
        {
            AdvanceScheduledUart1Receiver();
        }
    }

    void AdvanceTimerInterruptIfDue()
    {
        if (Cycles >= _nextTimerInterruptCycle)
        {
            AdvanceTimerInterrupt();
        }
    }

    void AdvanceExternalDspFrameIfDue()
    {
        if (Cycles >= _nextExternalDspFrameCycle)
        {
            AdvanceExternalDspFrame();
        }
    }

    void AdvancePeripheralEventsIfDue()
    {
        if (Cycles >= _nextPeripheralEventCycle)
        {
            AdvanceScheduledPeripheralEvents();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void AdvanceExternalDspFrame()
    {
        _nextExternalDspFrameCycle = long.MaxValue;
        ExternalDspFrameDue?.Invoke(Cycles);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void AdvanceScheduledPeripheralEvents()
    {
        while (_scheduledPeripheralEvents.Count > 0 &&
               _scheduledPeripheralEvents.Peek().Cycle <= Cycles)
        {
            ArmModemBusScheduledPeripheralEvent scheduled =
                _scheduledPeripheralEvents.Dequeue();
            if (!scheduled.IsCancelled)
            {
                scheduled.Callback(Cycles);
            }
        }

        DiscardCancelledPeripheralEvents();
        _nextPeripheralEventCycle = _scheduledPeripheralEvents.TryPeek(
            out _,
            out var nextPriority)
            ? nextPriority.Cycle
            : long.MaxValue;
    }

    void ScheduleOneShot(ushort ticks)
    {
        long generation = ++_oneShotGeneration;
        _oneShotEvent?.Dispose();
        _oneShotEvent = null;
        if (ticks == 0 || _byteRegisters[0x00800930] != 1)
        {
            return;
        }

        long dueCycle = Cycles + ticks * 130L;
        _oneShotEvent = SchedulePeripheralEvent(dueCycle, _ =>
        {
            if (generation != _oneShotGeneration ||
                _byteRegisters[0x00800930] != 1 ||
                _halfRegisters[0x0080071c] != ticks)
            {
                return;
            }
            _oneShotEvent = null;
            _irqPending |= OneShotInterruptBit;
            UpdateIrqLine();
        });
    }

    void DiscardCancelledPeripheralEvents()
    {
        while (_scheduledPeripheralEvents.Count > 0 &&
               _scheduledPeripheralEvents.Peek().IsCancelled)
        {
            _scheduledPeripheralEvents.Dequeue();
            if (_cancelledPeripheralEventCount > 0) _cancelledPeripheralEventCount--;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void AdvanceTimerInterrupt()
    {
        if (_timerInterruptPeriodCycles <= 0)
        {
            return;
        }

        _irqPending |= TimerInterruptBit;
        TimerInterruptCount++;
        do
        {
            _nextTimerInterruptCycle += _timerInterruptPeriodCycles;
        }
        while (_nextTimerInterruptCycle <= Cycles);
        UpdateIrqLine();
    }

    uint GetPendingInterrupts()
    {
        // FIFO3 TX-ready is a level source. The firmware keeps 0x800 masked
        // until it has staged an IrDA packet, fills the FIFO in its handler,
        // and masks the source again after writing the complete frame.
        uint pending = _irqPending;
        pending = AddInfraredTransmitInterrupt(pending);
        pending = AddUart1Interrupts(pending);
        pending = AddInfraredReceiveInterrupt(pending);
        return pending;
    }

    uint AddInfraredTransmitInterrupt(uint pending) =>
        _infraredTransmitReady
            ? pending | InfraredTransmitInterruptBit
            : pending;

    uint AddUart1Interrupts(uint pending)
    {
        if (!_uart1ReceiveEnabled)
        {
            return pending;
        }
        // The firmware's TX callback reads the 128-byte FIFO occupancy and
        // fills all available slots. Space-available and RX-ready are level
        // sources selected by its 0x200/0x400 masks.
        pending = _uart1Transmit.Count < Uart1TransmitFifoCapacity
            ? pending | Uart1TransmitInterruptBit
            : pending;
        return !_uart1Received.IsEmpty
            ? pending | Uart1ReceiveInterruptBit
            : pending;
    }

    uint AddInfraredReceiveInterrupt(uint pending) =>
        !_infraredReceived.IsEmpty
            ? pending | InfraredReceiveInterruptBit
            : pending;

    uint GetServiceableInterrupts() => GetPendingInterrupts() & ~_irqDisableMask;

    void AdvanceUart1Transmitter()
    {
        if (Cycles < _nextUart1TransmitCycle)
        {
            return;
        }

        if (_uart1Transmit.TryDequeue(out var transmission))
        {
            Uart1ByteTransmitted?.Invoke(transmission.Value);
        }
        _nextUart1TransmitCycle = _uart1Transmit.TryPeek(out var next)
            ? next.CompletionCycle
            : long.MaxValue;
        if (_nextUart1TransmitCycle == long.MaxValue)
        {
            _uart1TransmitTailCycle = Cycles;
        }
        UpdateIrqLine();
    }

    void AdvanceScheduledUart1Receiver()
    {
        while (_scheduledUart1Receive.TryPeek(out var receive, out var priority) &&
            priority.Cycle <= Cycles)
        {
            _scheduledUart1Receive.Dequeue();
            QueueUart1ReceivedByte(receive.Value);
        }
        _nextUart1ReceiveCycle = _scheduledUart1Receive.TryPeek(
            out _,
            out var nextPriority)
            ? nextPriority.Cycle
            : long.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UpdateIrqLine() =>
        _irqLineAsserted =
            (GetPendingInterrupts() & ~_irqDisableMask & 0x1fffffff) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UpdateNextBusEventCycle() =>
        _nextBusEventCycle = Math.Min(
            Math.Min(_nextUart1TransmitCycle, _nextUart1ReceiveCycle),
            Math.Min(
                Math.Min(_nextTimerInterruptCycle, _nextExternalDspFrameCycle),
                _nextPeripheralEventCycle));

    void ObserveDspPacketByte(byte value)
    {
        _dspPacketBytes.Add(value);
        if (_dspPacketBytes.Count < 2)
        {
            return;
        }

        int packetLength = _dspPacketBytes[1] + 2;
        if (_dspPacketBytes.Count < packetLength)
        {
            return;
        }
        if (_dspPacketBytes.Count > packetLength)
        {
            throw new InvalidOperationException("DSP packet exceeded its declared length.");
        }

        byte type = _dspPacketBytes[0];
        byte[] payload = _dspPacketBytes.Skip(2).ToArray();
        _dspPacketBytes.Clear();
        DspPacketTransmitted?.Invoke(new(type, payload));
    }

    void ObserveDspTransferControl(byte value)
    {
        if (!_dspTransferActive)
        {
            _dspTransferActive = true;
            _dspTransferControl = value;
            return;
        }

        if (_dspTransferLength >= 0 &&
            _dspTransferPayload.Count != _dspTransferLength)
        {
            throw new InvalidOperationException("DSP transfer ended before its declared length.");
        }

        byte? kind = _dspTransferLength >= 0 ? _dspTransferKind : null;
        byte[] payload = _dspTransferPayload.ToArray();
        byte control = _dspTransferControl;
        ResetDspTransfer();
        DspTransferTransmitted?.Invoke(new(control, kind, value, payload));
    }

    void ObserveDspTransferData(byte value)
    {
        if (!_dspTransferActive)
        {
            throw new InvalidOperationException("DSP transfer data arrived without control framing.");
        }

        if (_dspTransferLength < 0)
        {
            _dspTransferKind = (byte)(value & 7);
            _dspTransferLength = value >> 3;
            return;
        }

        if (_dspTransferPayload.Count >= _dspTransferLength)
        {
            throw new InvalidOperationException("DSP transfer exceeded its declared length.");
        }
        _dspTransferPayload.Add(value);
    }

    void ResetDspTransfer()
    {
        _dspTransferActive = false;
        _dspTransferControl = 0;
        _dspTransferKind = 0;
        _dspTransferLength = -1;
        _dspTransferPayload.Clear();
    }

    void ReconfigureTimerInterrupt()
    {
        byte clockEnabled = _byteRegisters[0x00800910];
        byte clockDivider = _byteRegisters[0x00800914];
        byte timerControl = _byteRegisters[0x00800708];
        byte timerPeriod = _byteRegisters[0x00800710];
        byte timerMode = _byteRegisters[0x00800718];

        // R8A015 selects this periodic Irma-B timer configuration during
        // startup. Its ISR acknowledges each tick by reading the period
        // register at 0x00800710.
        if (clockEnabled != 1 || clockDivider == 0 || timerControl != 4 ||
            timerPeriod == 0 || timerMode != 0x0b)
        {
            _timerInterruptPeriodCycles = 0;
            _nextTimerInterruptCycle = long.MaxValue;
            UpdateNextBusEventCycle();
            return;
        }

        // This exact R8A015 register tuple drives the OSE 5 ms system tick.
        // The ISR increments the counter returned as counter * 5 ms by
        // firmware function 0x01000650. 0xcb and 0x2d are encoded divider/
        // mode values, not a literal cycle-product period.
        _timerInterruptPeriodCycles = OseTimerInterruptPeriodCycles;
        _nextTimerInterruptCycle = Cycles + _timerInterruptPeriodCycles;
        UpdateNextBusEventCycle();
    }

    ArmModemMemoryRegion? Resolve(uint address, int size) =>
        ResolveLinearRegion(address)?.Resolve(address, size) ??
        ResolveExternalRam(address, size);

    ArmModemAddressRegion? ResolveLinearRegion(uint address) => address switch
    {
        < InternalRamSize => _internalRamRegion,
        >= 0x00800300 and < 0x00800324 => _bootScratchRegion,
        >= 0x00801000 and < 0x00801400 => _asicTableRegion,
        _ => null,
    };

    ArmModemMemoryRegion? ResolveExternalRam(uint address, int size) =>
        address >= ExternalRamBase &&
        address - ExternalRamBase + size <= ExternalRamMirrorSpan
            ? new(
                _externalRam,
                (int)((address - ExternalRamBase) & (ExternalRamSize - 1)))
            : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint ReadLittleEndian(byte[] backing, int offset, int size) => size switch
    {
        4 => BinaryPrimitives.ReadUInt32LittleEndian(backing.AsSpan(offset, 4)),
        2 => BinaryPrimitives.ReadUInt16LittleEndian(backing.AsSpan(offset, 2)),
        _ => backing[offset],
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void WriteLittleEndian(byte[] backing, int offset, uint value, int size)
    {
        switch (size)
        {
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(
                    backing.AsSpan(offset, 4), value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(
                    backing.AsSpan(offset, 2), (ushort)value);
                break;
            default:
                backing[offset] = (byte)value;
                break;
        }
    }

    uint GetCurrentPc() => CurrentPcProvider?.Invoke() ?? CurrentPc;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ObserveMmio(uint address, int size, bool isWrite, uint value)
    {
        var observer = MmioAccessed;
        if (observer is not null &&
            address is >= 0x00800000 and < 0x00802000)
        {
            observer(new(Cycles, GetCurrentPc(), address, size, isWrite, value));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DispatchExternalRamWriteObservers(ArmModemRamWrite write)
    {
        if (_externalRamWriteObservers.Count == 0)
        {
            return;
        }
        DispatchExternalRamWriteObserversSlow(write);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void DispatchExternalRamWriteObserversSlow(ArmModemRamWrite write)
    {
        foreach (ArmModemBusExternalRamWriteObserver observer in _externalRamWriteObservers)
        {
            if (!observer.IsDisposed && observer.Address == write.Address)
            {
                observer.Callback(write);
            }
        }
    }

}
