// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Mia.Emulator.Modem;

internal sealed class ArmModemLinkBridge : IDisposable
{
    readonly AsicByteChannels _byteChannels;
    readonly ArmModem _modem;
    readonly bool _bufferCoreEffects;
    readonly MiaSystemClock? _asicClock;
    readonly MiaWorker? _asicWorker;
    readonly MiaWorker? _modemWorker;
    readonly ConcurrentQueue<byte> _asicToModem = new();
    readonly ConcurrentQueue<byte> _modemToAsic = new();
    readonly ConcurrentQueue<ArmModemLinkBridgeScheduledByte> _scheduledModemToAsic = new();
    int _pendingAsicEffects;
    int _pendingModemEffects;
    int _pendingModemSchedules;
    bool _disposed;

    public ArmModemLinkBridge(
        AsicByteChannels byteChannels,
        ArmModem modem,
        bool bufferCoreEffects = false,
        MiaWorker? asicWorker = null,
        MiaWorker? modemWorker = null,
        MiaSystemClock? asicClock = null)
    {
        _byteChannels = byteChannels;
        _modem = modem;
        _bufferCoreEffects = bufferCoreEffects;
        _asicClock = asicClock;
        _asicWorker = asicWorker;
        _modemWorker = modemWorker;
        _byteChannels.ByteTransmitted += OnAsicByteTransmitted;
        _byteChannels.TransmissionScheduled += OnAsicTransmissionScheduled;
        _modem.Uart1ByteTransmitted += OnModemByteTransmitted;
        _modem.Bus.Uart1TransmissionScheduled += OnModemTransmissionScheduled;
    }

    public long AsicToModemByteCount { get; private set; }

    public long ModemToAsicByteCount { get; private set; }

    /// <summary>
    /// Commits AVR-to-ARM UART effects after both core grants have stopped.
    /// </summary>
    public void CommitAsicEffects()
    {
        if (Interlocked.Exchange(ref _pendingAsicEffects, 0) != 0)
        {
            Invoke(_modemWorker, CommitAsicEffectsCore);
        }
    }

    void CommitAsicEffectsCore()
    {
        while (_asicToModem.TryDequeue(out byte value))
        {
            _modem.QueueUart1ReceivedByte(value);
        }
    }

    /// <summary>
    /// Commits the ARM worker's timestamp-ordered UART effects on the ASIC
    /// owner at a completed core grant. This is a barrier effect journal, not a
    /// periodic peripheral poll.
    /// </summary>
    public void CommitModemEffects()
    {
        if (Interlocked.Exchange(ref _pendingModemEffects, 0) != 0)
        {
            Invoke(_asicWorker, CommitModemEffectsCore);
        }
    }

    /// <summary>
    /// Installs ARM serializer completions in the AVR-owned ASIC clock after
    /// the ARM grant returns. Cycle-locked mode calls this at that core barrier.
    /// </summary>
    public void CommitModemSchedules()
    {
        if (_asicClock is null ||
            Volatile.Read(ref _pendingModemSchedules) == 0 ||
            Interlocked.Exchange(ref _pendingModemSchedules, 0) == 0)
        {
            return;
        }

        while (_scheduledModemToAsic.TryDequeue(out var transmission))
        {
            Action deliver = () =>
                _byteChannels.QueueReceivedByte(1, transmission.Value);
            if (transmission.ArrivalCycle <= _asicClock.Cycles)
            {
                // The ARM grant may have produced this UART completion while
                // the AVR crossed its timestamp. Commit it at this completed
                // ASIC-owner barrier rather than trying to insert a clock
                // event in the past.
                deliver();
                continue;
            }
            if (_asicWorker is null)
            {
                _asicClock.ScheduleAt(deliver, transmission.ArrivalCycle);
            }
            else
            {
                _asicClock.ScheduleAt(
                    _asicWorker,
                    deliver,
                    transmission.ArrivalCycle);
            }
        }
    }

    void CommitModemEffectsCore()
    {
        while (_modemToAsic.TryDequeue(out byte value))
        {
            _byteChannels.QueueReceivedByte(1, value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _byteChannels.ByteTransmitted -= OnAsicByteTransmitted;
        _byteChannels.TransmissionScheduled -= OnAsicTransmissionScheduled;
        _modem.Uart1ByteTransmitted -= OnModemByteTransmitted;
        _modem.Bus.Uart1TransmissionScheduled -= OnModemTransmissionScheduled;
        _disposed = true;
    }

    void OnAsicByteTransmitted(int channel, byte value)
    {
        if (channel != 1)
        {
            return;
        }

        AsicToModemByteCount++;
        if (_asicClock is not null && !_bufferCoreEffects)
        {
            return;
        }
        if (_bufferCoreEffects)
        {
            _asicToModem.Enqueue(value);
            Interlocked.Increment(ref _pendingAsicEffects);
        }
        else
        {
            Invoke(_modemWorker, () => _modem.QueueUart1ReceivedByte(value));
        }
    }

    void OnAsicTransmissionScheduled(int channel, byte value, long arrivalCycle)
    {
        if (channel != 1 || _asicClock is null || _bufferCoreEffects)
        {
            return;
        }

        if (_modemWorker is null)
        {
            _modem.Bus.ScheduleUart1ReceivedByte(value, arrivalCycle);
        }
        else
        {
            _modemWorker.Post(
                () => _modem.Bus.ScheduleUart1ReceivedByte(value, arrivalCycle));
        }
    }

    void OnModemByteTransmitted(byte value)
    {
        ModemToAsicByteCount++;
        if (_asicClock is not null && !_bufferCoreEffects)
        {
            return;
        }
        if (_bufferCoreEffects)
        {
            _modemToAsic.Enqueue(value);
            Interlocked.Increment(ref _pendingModemEffects);
        }
        else
        {
            Invoke(_asicWorker, () => _byteChannels.QueueReceivedByte(1, value));
        }
    }

    void OnModemTransmissionScheduled(byte value, long arrivalCycle)
    {
        if (_asicClock is null || _bufferCoreEffects)
        {
            return;
        }

        _scheduledModemToAsic.Enqueue(new(value, arrivalCycle));
        Interlocked.Increment(ref _pendingModemSchedules);
    }

    static void Invoke(MiaWorker? worker, Action action)
    {
        if (worker is null || worker.IsCurrentThread)
        {
            action();
        }
        else
        {
            worker.Invoke(action);
        }
    }
}
