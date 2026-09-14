// SPDX-License-Identifier: MIT

using System.IO.Compression;

namespace Mia.Emulator.Diagnostics;

/// <summary>
/// Aggregates AVR data-bus, primary-I2C, and ARM MMIO accesses for one fixed
/// ASIC-cycle window. Register-to-register AVR operations do not cross the
/// data bus and are intentionally outside this diagnostic boundary.
/// </summary>
internal sealed class AccessTraceSession : IDisposable
{
    readonly MiaMachine _machine;
    readonly Cpu _cpu;
    readonly ArmModemBus? _armBus;
    readonly AccessTraceAccumulator _accumulator;
    readonly PrimaryI2cTraceDecoder _primaryI2c;
    readonly CpuDataReadObserver _readObserver;
    readonly CpuDataWriteObserver _writeObserver;
    readonly Action<ArmModemMmioAccess> _mmioObserver;
    readonly long _avrToAsicOffset;
    readonly long _startCycle;
    readonly long _endCycleExclusive;
    int _stopState;
    long _stoppedAtCycle;

    public AccessTraceSession(
        MiaMachine machine,
        long startCycle,
        long endCycleExclusive)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _machine = machine;
        var window = new AccessTraceWindow(startCycle, endCycleExclusive);
        _startCycle = startCycle;
        _endCycleExclusive = endCycleExclusive;
        machine.Diagnostics.DisableFirmwareFastPaths();
        (_cpu, _avrToAsicOffset) = machine.Diagnostics.InspectAvr(cpu =>
        {
            long offset = checked(
                machine.Cycles - AccessTraceClockConversion.AvrToAsic(cpu.Cycles));
            return (cpu, offset);
        });
        var clock = new AccessTraceClockInfo(
            "asic-13mhz",
            MiaSystemClock.AsicCyclesPerSecond,
            MiaSystemClock.AvrCyclesPerSecond,
            AccessTraceClockConversion.AvrToAsicNumerator,
            AccessTraceClockConversion.AvrToAsicDenominator,
            _avrToAsicOffset);
        _accumulator = new AccessTraceAccumulator(window, clock);
        _primaryI2c = new PrimaryI2cTraceDecoder(
            _accumulator.RecordPrimaryI2c);
        _readObserver = OnAvrRead;
        _writeObserver = OnAvrWrite;
        _mmioObserver = OnArmMmio;

        machine.Diagnostics.InspectAvr(cpu =>
        {
            cpu.DataReadObserver = _readObserver + cpu.DataReadObserver;
            cpu.DataWriteObserver = _writeObserver + cpu.DataWriteObserver;
            return true;
        });
        _armBus = machine.Modem?.Bus;
        if (_armBus is not null)
        {
            _armBus.MmioAccessed += _mmioObserver;
        }
    }

    public AccessTraceSnapshot Snapshot()
    {
        ThrowIfRunning();
        return _accumulator.Snapshot() with
        {
            Capture = new(
                Stopped: true,
                StoppedAtCycle: Volatile.Read(ref _stoppedAtCycle)),
        };
    }

    public void WriteJson(Stream destination)
    {
        AccessTraceJson.Write(destination, Snapshot());
    }

    public void WriteJson(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var output = File.Create(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gzip = new GZipStream(
                output,
                CompressionLevel.SmallestSize,
                leaveOpen: true);
            WriteJson(gzip);
        }
        else
        {
            WriteJson(output);
        }
    }

    /// <summary>
    /// Detaches immediately at the caller's guest boundary. Aggregate
    /// snapshots remain available after workers become quiescent.
    /// </summary>
    public void Stop() => Stop(GetAvrAsicCycle());

    public void Stop(long stoppedAtAsicCycle)
    {
        if (Interlocked.Exchange(ref _stopState, 1) != 0)
        {
            return;
        }
        Volatile.Write(ref _stoppedAtCycle, stoppedAtAsicCycle);
        if (_armBus is not null)
        {
            _armBus.MmioAccessed -= _mmioObserver;
        }
        _machine.Diagnostics.InspectAvr(cpu =>
        {
            cpu.DataReadObserver = (CpuDataReadObserver?)Delegate.Remove(
                cpu.DataReadObserver,
                _readObserver);
            cpu.DataWriteObserver = (CpuDataWriteObserver?)Delegate.Remove(
                cpu.DataWriteObserver,
                _writeObserver);
            return true;
        });
    }

    public void Dispose() => Stop();

    void OnAvrRead(int address, byte value)
    {
        if (Volatile.Read(ref _stopState) != 0)
        {
            return;
        }
        long cycle = ClampToWindow(GetAvrAsicCycle());
        int pc = _cpu.PC;
        _accumulator.RecordAvrRead(cycle, pc, address, value);
        _primaryI2c.ObserveRead(cycle, pc, address, value);
    }

    void OnAvrWrite(
        int address,
        byte oldValue,
        byte newValue,
        byte mask,
        bool hookConsumed)
    {
        if (Volatile.Read(ref _stopState) != 0)
        {
            return;
        }
        long cycle = ClampToWindow(GetAvrAsicCycle());
        int pc = _cpu.PC;
        _accumulator.RecordAvrWrite(
            cycle,
            pc,
            address,
            oldValue,
            newValue,
            mask,
            hookConsumed);
        byte effectiveValue = hookConsumed
            ? newValue
            : (byte)((oldValue & ~mask) | (newValue & mask));
        _primaryI2c.ObserveWrite(cycle, pc, address, effectiveValue);
    }

    void OnArmMmio(ArmModemMmioAccess access)
    {
        if (Volatile.Read(ref _stopState) == 0)
        {
            _accumulator.RecordArmMmio(access with
            {
                Cycle = ClampToWindow(access.Cycle),
            });
        }
    }

    // Scheduler callbacks and worker batches fire on chunk boundaries, so a
    // bounded number of observations can arrive outside the window edges
    // while the observers are attached. They belong to the nearest covered
    // bin.
    long ClampToWindow(long cycle)
    {
        if (cycle < _startCycle)
        {
            return _startCycle;
        }
        return cycle >= _endCycleExclusive ? _endCycleExclusive - 1 : cycle;
    }

    void ThrowIfRunning()
    {
        if (Volatile.Read(ref _stopState) == 0)
        {
            throw new InvalidOperationException(
                "Stop the access trace before taking its quiescent snapshot.");
        }
    }

    long GetAvrAsicCycle() => checked(
        AccessTraceClockConversion.AvrToAsic(_cpu.Cycles) +
        _avrToAsicOffset);
}
