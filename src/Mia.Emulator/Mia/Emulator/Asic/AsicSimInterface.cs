// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Byte-level ASIC interface between the AVR and a SIM card. Firmware reads
/// the receive FIFO count from 0x0aa4, reads and writes bytes through 0x0aa0,
/// and advances transmission through the SIMRX_LOW and SIMTX logical IRQs.
/// </summary>
internal sealed class AsicSimInterface
{
    public const int DataAddress = 0x0aa0;
    public const int ControlAddress = 0x0aa2;
    public const int StatusAddress = 0x0aa4;
    public const int CountShift = 3;
    public const int MaximumReportedCount = 0x1f;
    internal const int MaximumReceiveBacklog = 512;

    // At the initial Fi=372 rate, one 10-ETU character takes 3,720 card
    // clocks. The observed initial prescaler is four 13 MHz ASIC-reference
    // ticks per SIM clock (0x0aa6 = 2), yielding 14,880 ticks per character.
    public const int InitialCharacterCycles = 10 * 372 * 4;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController _interruptController;
    readonly MiaWorker? _worker;
    readonly Queue<byte> _received = new();

    public AsicSimInterface(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController interruptController,
        int transmitCompletionCycles = InitialCharacterCycles,
        MiaWorker? worker = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transmitCompletionCycles);

        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _worker = worker;
        TransmitCompletionCycles = transmitCompletionCycles;
        cpu.ReadHooks[StatusAddress] = ReadStatus;
        cpu.ReadHooks[DataAddress] = ReadData;
        cpu.WriteHooks[DataAddress] = WriteData;
        cpu.ReadHooks[ControlAddress] = _ => Control;
        cpu.WriteHooks[ControlAddress] = WriteControl;
    }

    public int TransmitCompletionCycles { get; }

    public byte Control { get; private set; }

    public int QueuedByteCount => Invoke(() => _received.Count);

    public long ReceivedByteCount { get; private set; }

    public long TransmittedByteCount { get; private set; }

    public event Action<byte>? ByteReceived;

    public event Action<byte>? ByteTransmitted;

    public event Action<byte, byte>? ControlChanged;

    public void QueueReceivedByte(byte value) =>
        Invoke(() => QueueReceivedByteCore(value));

    void QueueReceivedByteCore(byte value)
    {
        if (_received.Count >= MaximumReceiveBacklog) return;
        var wasEmpty = _received.Count == 0;
        _received.Enqueue(value);
        if (wasEmpty)
        {
            _interruptController.RaiseHighPriority(AsicInterruptController.SimRxLowSource);
        }
    }

    public void ScheduleReceivedBytes(
        IEnumerable<byte> values,
        int initialDelayCycles,
        int? interCharacterCycles = null)
        => Invoke(() => ScheduleReceivedBytesCore(
            values,
            initialDelayCycles,
            interCharacterCycles));

    void ScheduleReceivedBytesCore(
        IEnumerable<byte> values,
        int initialDelayCycles,
        int? interCharacterCycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialDelayCycles);

        var spacing = interCharacterCycles ?? TransmitCompletionCycles;
        if (spacing <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interCharacterCycles));
        }

        var delay = initialDelayCycles;
        foreach (var value in values)
        {
            var scheduledValue = value;
            Schedule(() => QueueReceivedByte(scheduledValue), delay);
            delay += spacing;
        }
    }

    byte ReadStatus(int address)
    {
        var count = Math.Min(_received.Count, MaximumReportedCount);
        return (byte)((_cpu.Data[address] & 0x07) | count << CountShift);
    }

    byte ReadData(int address)
    {
        if (!_received.TryDequeue(out var value))
        {
            return _cpu.Data[address];
        }

        ReceivedByteCount++;
        ByteReceived?.Invoke(value);
        return value;
    }

    bool WriteData(byte value, byte _, int __, byte ___)
    {
        Schedule(() => CompleteTransmission(value), TransmitCompletionCycles);
        return false;
    }

    bool WriteControl(byte value, byte oldValue, int _, byte mask)
    {
        var newValue = (byte)((oldValue & ~mask) | (value & mask));
        Control = newValue;
        ControlChanged?.Invoke(oldValue, newValue);
        return false;
    }

    void CompleteTransmission(byte value)
    {
        TransmittedByteCount++;
        _interruptController.RaiseHighPriority(AsicInterruptController.SimTransmitSource);
        ByteTransmitted?.Invoke(value);
    }

    void Schedule(Action callback, long delayCycles)
    {
        if (_worker is null)
        {
            _clock.Schedule(callback, delayCycles);
        }
        else
        {
            _clock.Schedule(_worker, callback, delayCycles);
        }
    }

    void Invoke(Action action)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            action();
        }
        else
        {
            _worker.Invoke(action);
        }
    }

    TResult Invoke<TResult>(Func<TResult> function)
    {
        if (_worker is null || _worker.IsCurrentThread)
        {
            return function();
        }
        return _worker.Invoke(function);
    }
}
