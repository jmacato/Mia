// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

internal sealed class AsicByteChannels
{
    // One 8N1 character at the handset's 13 MHz reference and the firmware's
    // recovered 460800-baud link rate, rounded to the nearest ASIC tick.
    public const int LinkTransmitCharacterCycles = 282;
    internal const int MaximumReceiveBacklog = 4096;

    const byte ReceiveReady = 0x02;
    const byte TransmitReady = 0x02;
    const byte TransmitComplete = 0x40;
    const byte ReceiveWindowInterruptEnable = 0x40;
    const byte TransmitReadyInterruptEnable = 0x02;
    const int ReceiveCountOffset = 6;
    const int TransmitCountOffset = 7;
    const int ReceiveWindowOffset = 0x0a;
    static readonly int[] Bases = [0x0900, 0x0910];

    readonly Queue<byte>[] _receiveQueues = [new(), new()];
    readonly int[] _receiveWindowRemaining = new int[Bases.Length];
    readonly bool[] _receiveInterruptArmed = new bool[Bases.Length];
    readonly bool[] _receiveInterruptOutstanding = new bool[Bases.Length];
    readonly bool[] _accessoryConnected = new bool[Bases.Length];
    readonly int[] _accessoryGeneration = new int[Bases.Length];
    readonly bool[] _accessoryTransmitReady = new bool[Bases.Length];
    readonly long[] _receiveCounts = new long[Bases.Length];
    readonly long[] _receiveInterruptCounts = new long[Bases.Length];
    readonly long[] _transmitCounts = new long[Bases.Length];
    readonly long[] _transmitInterruptCounts = new long[Bases.Length];
    readonly int[] _transmitBurstLengths = new int[Bases.Length];
    readonly long[] _transmitTailCycles = new long[Bases.Length];
    readonly long[] _nextTransmitInterruptCycles =
        Enumerable.Repeat(long.MaxValue, Bases.Length).ToArray();
    long _nextTransmitInterruptCycle = long.MaxValue;
    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    readonly AsicInterruptController? _interruptController;
    readonly Action _serviceTransmitInterrupts;
    readonly MiaWorker? _worker;
    bool _transmitServiceScheduled;
    int _postedTransmitWrites;

    public AsicByteChannels(
        Cpu cpu,
        MiaSystemClock clock,
        AsicInterruptController? interruptController = null,
        MiaWorker? worker = null)
    {
        _cpu = cpu;
        _clock = clock;
        _interruptController = interruptController;
        _worker = worker;
        _serviceTransmitInterrupts = ServiceTransmitInterrupts;
        for (var channel = 0; channel < Bases.Length; channel++)
        {
            var capturedChannel = channel;
            var completionAddress = Bases[channel] + 3;
            var controlAddress = Bases[channel] + 1;
            var statusAddress = Bases[channel] + 4;
            var transmitAddress = Bases[channel] + 5;
            var receiveCountAddress = Bases[channel] + ReceiveCountOffset;
            var transmitCountAddress = Bases[channel] + TransmitCountOffset;
            var receiveWindowAddress = Bases[channel] + ReceiveWindowOffset;
            var receiveInterruptEnableAddress = Bases[channel] + 8;
            var transmitInterruptEnableAddress = Bases[channel] + 9;
            cpu.ReadHooks[completionAddress] = address => (byte)(
                cpu.Data[address] |
                TransmitComplete |
                (_receiveQueues[capturedChannel].Count > 0 ? ReceiveReady : 0) |
                (_accessoryConnected[capturedChannel] ? 0x80 : 0));
            cpu.WriteHooks[controlAddress] = (value, oldValue, _, mask) =>
            {
                if (capturedChannel == 1 && (value & mask & TransmitComplete) != 0)
                {
                    _receiveInterruptOutstanding[capturedChannel] = false;
                    TryRaiseReceiveInterrupt(
                        capturedChannel,
                        _cpu.Data[Bases[capturedChannel] + 8]);
                }
                return false;
            };
            cpu.ReadHooks[statusAddress] = address => (byte)(
                cpu.Data[address] |
                TransmitReady |
                (_accessoryTransmitReady[capturedChannel] ? 0x01 : 0));
            cpu.ReadHooks[transmitAddress] = address =>
            {
                if (_receiveQueues[capturedChannel].TryDequeue(out var value))
                {
                    _receiveCounts[capturedChannel]++;
                    ByteReceived?.Invoke(capturedChannel, value);
                    return value;
                }

                return cpu.Data[address];
            };
            cpu.WriteHooks[transmitAddress] = (value, _, _, _) =>
            {
                TransmitByte(capturedChannel, value, _clock.Cycles);
                return false;
            };
            cpu.ReadHooks[receiveCountAddress] = _ =>
                (byte)Math.Min(_receiveQueues[capturedChannel].Count, byte.MaxValue);
            // Writes reach this address during the ASIC's generic channel
            // reset, but firmware reads it as the live TX FIFO occupancy.
            // Bytes currently drain directly into the connected peer.
            cpu.ReadHooks[transmitCountAddress] = _ => 0;
            cpu.WriteHooks[receiveWindowAddress] = (value, _, _, _) =>
            {
                _receiveWindowRemaining[capturedChannel] = value;
                _receiveInterruptArmed[capturedChannel] = value != 0;
                AccountForQueuedReceiveBytes(capturedChannel);
                ReceiveWindowChanged?.Invoke(capturedChannel, value);
                return false;
            };
            cpu.WriteHooks[receiveInterruptEnableAddress] = (value, oldValue, _, mask) =>
            {
                byte newValue = (byte)((oldValue & ~mask) | (value & mask));
                TryRaiseReceiveInterrupt(capturedChannel, newValue);
                return false;
            };
            cpu.WriteHooks[transmitInterruptEnableAddress] = (value, oldValue, _, mask) =>
            {
                byte newValue = (byte)((oldValue & ~mask) | (value & mask));
                ScheduleTransmitInterrupt(capturedChannel, newValue);
                return false;
            };
        }
    }

    public event Action<int, byte>? ByteTransmitted;

    /// <summary>
    /// Raised when a byte enters the serializer. The arrival cycle is in the
    /// shared 13 MHz ASIC/ARM domain and includes queued characters.
    /// </summary>
    public event Action<int, byte, long>? TransmissionScheduled;

    public event Action<int, byte>? ByteReceived;

    public event Action<int, byte>? ReceiveWindowChanged;

    /// <summary>
    /// Replaces the two TX-register hooks with non-blocking bus writes. Each
    /// byte retains its firmware write cycle and is processed in FIFO order by
    /// the byte-channel owner; reads and other bound writes naturally act as
    /// barriers because they enter the same worker mailbox afterward.
    /// </summary>
    internal void EnablePostedTransmitWrites()
    {
        if (_worker is null)
        {
            return;
        }
        for (var channel = 0; channel < Bases.Length; channel++)
        {
            var capturedChannel = channel;
            var transmitAddress = Bases[channel] + 5;
            _cpu.WriteHooks[transmitAddress] = (value, _, _, _) =>
            {
                var writeCycle = _clock.Cycles;
                Interlocked.Increment(ref _postedTransmitWrites);
                _worker.Post(() =>
                {
                    try
                    {
                        TransmitByte(capturedChannel, value, writeCycle);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _postedTransmitWrites);
                    }
                });
                return false;
            };
        }
    }

    /// <summary>
    /// Waits only when posted TX writes are still outstanding. Coarse core
    /// grants call this once before publishing the accumulated UART effects.
    /// </summary>
    internal void FlushPostedTransmitWrites()
    {
        if (_worker is null || Volatile.Read(ref _postedTransmitWrites) == 0)
        {
            return;
        }
        _worker.Invoke(() => { });
    }

    public void QueueReceivedByte(int channel, byte value) =>
        Invoke(() => QueueReceivedByteCore(channel, value));

    public void ScheduleReceivedBytes(
        int channel,
        ReadOnlySpan<byte> values,
        long delayCycles,
        long interByteCycles = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delayCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(interByteCycles);
        byte[] copy = values.ToArray();
        Invoke(() =>
        {
            int generation = _accessoryGeneration[channel];
            if (interByteCycles == 0)
            {
                Schedule(copy, delayCycles);
                return;
            }

            for (var index = 0; index < copy.Length; index++)
            {
                long byteDelay = checked(delayCycles + index * interByteCycles);
                Schedule([copy[index]], byteDelay);
            }

            void Schedule(byte[] scheduledValues, long scheduledDelay)
            {
                _clock.Schedule(() =>
                {
                    void QueueValues()
                    {
                        if (!_accessoryConnected[channel] ||
                            _accessoryGeneration[channel] != generation)
                        {
                            return;
                        }
                        foreach (byte value in scheduledValues)
                        {
                            QueueReceivedByteCore(channel, value);
                        }
                    }

                    if (_worker is null)
                    {
                        QueueValues();
                    }
                    else
                    {
                        _worker.Post(QueueValues);
                    }
                }, scheduledDelay);
            }
        });
    }

    public void ClearReceivedBytes(int channel) => Invoke(() =>
    {
        _receiveQueues[channel].Clear();
        _receiveWindowRemaining[channel] = 0;
        _receiveInterruptArmed[channel] = false;
    });

    /// <summary>
    /// Drives the physical-ready inputs used by the native accessory-channel
    /// activation and transmit paths.
    /// </summary>
    public void SetAccessoryConnected(int channel, bool connected) =>
        Invoke(() =>
        {
            _accessoryConnected[channel] = connected;
            _accessoryGeneration[channel]++;
            int completionAddress = Bases[channel] + 3;
            _cpu.Data[completionAddress] = connected
                ? (byte)(_cpu.Data[completionAddress] | 0x80)
                : (byte)(_cpu.Data[completionAddress] & ~0x80);
            if (!connected)
            {
                _accessoryTransmitReady[channel] = false;
                _cpu.Data[Bases[channel] + 4] &= 0xfe;
            }
        });

    public void SetAccessoryTransmitReady(int channel, bool ready) =>
        Invoke(() => SetAccessoryTransmitReadyCore(channel, ready));

    public void ScheduleAccessoryTransmitReady(int channel, long delayCycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delayCycles);
        Invoke(() =>
        {
            int generation = _accessoryGeneration[channel];
            _clock.Schedule(() =>
            {
                void ApplyReady()
                {
                    if (_accessoryConnected[channel] &&
                        _accessoryGeneration[channel] == generation)
                    {
                        SetAccessoryTransmitReadyCore(channel, true);
                    }
                }

                if (_worker is null)
                {
                    ApplyReady();
                }
                else
                {
                    _worker.Post(ApplyReady);
                }
            }, delayCycles);
        });
    }

    void SetAccessoryTransmitReadyCore(int channel, bool ready)
    {
        _accessoryTransmitReady[channel] = ready;
        int statusAddress = Bases[channel] + 4;
        _cpu.Data[statusAddress] = ready
            ? (byte)(_cpu.Data[statusAddress] | 0x01)
            : (byte)(_cpu.Data[statusAddress] & ~0x01);
    }

    void QueueReceivedByteCore(int channel, byte value)
    {
        // Retain unread bytes on overrun. A stalled guest cannot turn a
        // peripheral FIFO into an ever-growing host-side serial recording.
        if (_receiveQueues[channel].Count >= MaximumReceiveBacklog) return;
        _receiveQueues[channel].Enqueue(value);
        if (_receiveWindowRemaining[channel] > 0)
        {
            _receiveWindowRemaining[channel]--;
        }
        TryRaiseReceiveInterrupt(channel, _cpu.Data[Bases[channel] + 8]);
    }

    void TransmitByte(int channel, byte value, long writeCycle)
    {
        _transmitCounts[channel]++;
        _transmitBurstLengths[channel]++;
        long arrivalCycle = Math.Max(
            writeCycle,
            _transmitTailCycles[channel]) +
            LinkTransmitCharacterCycles;
        _transmitTailCycles[channel] = arrivalCycle;
        TransmissionScheduled?.Invoke(channel, value, arrivalCycle);
        ByteTransmitted?.Invoke(channel, value);
    }

    public int GetReceiveQueueLength(int channel) =>
        Invoke(() => _receiveQueues[channel].Count);

    public int GetReceiveWindowRemaining(int channel) =>
        Invoke(() => _receiveWindowRemaining[channel]);

    public long GetReceiveCount(int channel) => Invoke(() => _receiveCounts[channel]);

    public long GetReceiveInterruptCount(int channel) =>
        Invoke(() => _receiveInterruptCounts[channel]);

    public long GetTransmitCount(int channel) => Invoke(() => _transmitCounts[channel]);

    public long GetTransmitInterruptCount(int channel) =>
        Invoke(() => _transmitInterruptCounts[channel]);

    void ServiceTransmitInterrupts()
    {
        _transmitServiceScheduled = false;
        _nextTransmitInterruptCycle = long.MaxValue;
        for (var channel = 0; channel < Bases.Length; channel++)
        {
            if (_clock.Cycles < _nextTransmitInterruptCycles[channel])
            {
                continue;
            }

            _nextTransmitInterruptCycles[channel] = long.MaxValue;
            if ((_cpu.Data[Bases[channel] + 9] & TransmitReadyInterruptEnable) != 0)
            {
                RaiseTransmitInterrupt(channel);
            }
        }
        RecalculateNextTransmitInterruptCycle();
        ScheduleTransmitService();
    }

    void AccountForQueuedReceiveBytes(int channel)
    {
        _receiveWindowRemaining[channel] = Math.Max(
            0,
            _receiveWindowRemaining[channel] - _receiveQueues[channel].Count);
        TryRaiseReceiveInterrupt(channel, _cpu.Data[Bases[channel] + 8]);
    }

    void TryRaiseReceiveInterrupt(int channel, byte interruptEnable)
    {
        if (channel != 1 || !_receiveInterruptArmed[channel] ||
            _receiveInterruptOutstanding[channel] ||
            _receiveWindowRemaining[channel] != 0 ||
            (interruptEnable & ReceiveWindowInterruptEnable) == 0)
        {
            return;
        }

        _receiveInterruptArmed[channel] = false;
        _receiveInterruptOutstanding[channel] = true;
        _receiveInterruptCounts[channel]++;
        _interruptController?.RaiseHighPriority(AsicInterruptController.LinkReceiveSource);
    }

    void ScheduleTransmitInterrupt(int channel, byte interruptEnable)
    {
        if (channel != 1 || (interruptEnable & TransmitReadyInterruptEnable) == 0)
        {
            _nextTransmitInterruptCycles[channel] = long.MaxValue;
            RecalculateNextTransmitInterruptCycle();
            ScheduleTransmitService();
            return;
        }

        int characterCount = Math.Max(1, _transmitBurstLengths[channel]);
        _transmitBurstLengths[channel] = 0;
        _nextTransmitInterruptCycles[channel] =
            _clock.Cycles + (long)characterCount * LinkTransmitCharacterCycles;
        RecalculateNextTransmitInterruptCycle();
        ScheduleTransmitService();
    }

    void RecalculateNextTransmitInterruptCycle()
    {
        _nextTransmitInterruptCycle = long.MaxValue;
        foreach (var cycle in _nextTransmitInterruptCycles)
        {
            _nextTransmitInterruptCycle = Math.Min(_nextTransmitInterruptCycle, cycle);
        }
    }

    void ScheduleTransmitService()
    {
        if (_transmitServiceScheduled)
        {
            _clock.Cancel(_serviceTransmitInterrupts);
            _transmitServiceScheduled = false;
        }
        if (_nextTransmitInterruptCycle == long.MaxValue)
        {
            return;
        }

        var delay = Math.Max(1, _nextTransmitInterruptCycle - _clock.Cycles);
        if (_worker is null)
        {
            _clock.Schedule(_serviceTransmitInterrupts, delay);
        }
        else
        {
            _clock.Schedule(_worker, _serviceTransmitInterrupts, delay);
        }
        _transmitServiceScheduled = true;
    }

    void RaiseTransmitInterrupt(int channel)
    {
        _transmitInterruptCounts[channel]++;
        _interruptController?.RaiseHighPriority(AsicInterruptController.LinkTransmitSource);
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
