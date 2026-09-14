// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Arm7Core;

namespace Mia.Emulator.Modem;

internal sealed class ArmModem
{
    readonly List<ArmModemInstructionObserver> _instructionObservers = [];

    public ArmModem(ArmModemImage image, ArmModemFlashProfile flashProfile)
        : this(image, image.Payload, flashProfile)
    {
    }

    public ArmModem(ReadOnlySpan<byte> bih, ArmModemFlashProfile flashProfile)
        : this(
            ArmModemImage.ParseBihMetadata(bih),
            bih[ArmModemImage.HeaderLength..],
            flashProfile)
    {
    }

    ArmModem(
        ArmModemImage image,
        ReadOnlySpan<byte> payload,
        ArmModemFlashProfile flashProfile)
    {
        if (image.LoadAddress != ArmModemFlash.BaseAddress)
        {
            throw new InvalidDataException(
                $"ARM modem BIH load address 0x{image.LoadAddress:x8} is unsupported; " +
                $"the recovered flash mapping starts at 0x{ArmModemFlash.BaseAddress:x8}.");
        }

        Image = image;
        Bus = new ArmModemBus(payload, flashProfile);
        Cpu = new Arm7Tdmi(Bus);
        RomServices = new ArmModemRomServices();
        Cpu.SetCodeFetchHandler(0x00c00000, 0x00100000, TryHandleRomFetch);
        Bus.CurrentPcProvider = () => CurrentInstructionAddress;
        Cpu.UndefinedInstructionObserved += OnUndefinedInstructionObserved;

        uint entry = image.LoadAddress;
        Cpu.SetGpr(15, entry + 8);
        Cpu.PrimePipeline(
            Bus.ReadWord(entry, ArmAccess.Code | ArmAccess.None),
            Bus.ReadWord(entry + 4, ArmAccess.Code | ArmAccess.Sequential),
            ArmAccess.Code | ArmAccess.Sequential);
    }

    public ArmModemImage Image { get; }

    public ArmModemBus Bus { get; }

    public Arm7Tdmi Cpu { get; }

    public ArmModemRomServices RomServices { get; }

    public long Instructions { get; private set; }

    public long Cycles => Bus.Cycles;

    public ArmModemStopKind StopKind { get; private set; }

    public string? StopReason { get; private set; }

    public bool IsStopped => StopKind != ArmModemStopKind.None;

    public bool IsSleeping => Bus.SleepRequested;

    public uint CurrentInstructionAddress =>
        (Cpu.CpsrValue & 0x20) != 0 ? Cpu.GetGpr(15) - 4 : Cpu.GetGpr(15) - 8;

    public event Action<byte>? Uart1ByteTransmitted
    {
        add => Bus.Uart1ByteTransmitted += value;
        remove => Bus.Uart1ByteTransmitted -= value;
    }

    public event Action<ArmModem>? InstructionExecuting;

    internal bool HasGeneralInstructionObservers =>
        InstructionExecuting is not null;

    /// <summary>
    /// Observes one recovered firmware instruction boundary without invoking a
    /// general callback for every instruction.
    /// </summary>
    internal IDisposable ObserveInstruction(
        uint address,
        Action<ArmModem> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var observer = new ArmModemInstructionObserver(
            address,
            callback,
            RemoveInstructionObserver);
        _instructionObservers.Add(observer);
        return observer;
    }

    void RemoveInstructionObserver(ArmModemInstructionObserver observer) =>
        _instructionObservers.Remove(observer);

    public void QueueUart1ReceivedByte(byte value) => Bus.QueueUart1ReceivedByte(value);

    public void Step()
    {
        if (IsStopped)
        {
            return;
        }

        try
        {
            StepCore();
        }
        catch (ArmModemRomCallException call)
        {
            HandleRomCall(call);
        }
        catch (ArmModemBusAccessException error)
        {
            HandleBusError(error);
        }
    }

    public void RunUntilCycle(long cycleLimit)
    {
        while (!IsStopped && Cycles < cycleLimit)
        {
            bool interruptAccepted =
                Bus.IrqLineAsserted && (Cpu.CpsrValue & 0x80) == 0;
            if (Bus.SleepRequested && !interruptAccepted)
            {
                Bus.IdleUntilCycle(cycleLimit);
                continue;
            }

            try
            {
                if (Cpu.GetGpr(15) == 0x010e2dd4 && TryFastForwardTimerPoll(cycleLimit))
                {
                    continue;
                }
                StepCore();
            }
            catch (ArmModemRomCallException call)
            {
                HandleRomCall(call);
            }
            catch (ArmModemBusAccessException error)
            {
                HandleBusError(error);
            }
        }
    }

    internal bool TryFastForwardTimerPoll(long cycleLimit)
    {
        // This Thumb loop only rereads a flag and the free-running timer.
        // A complete unchanged iteration takes 23 cycles and leaves every
        // register, flag and pipeline slot unchanged. Stop before either the
        // counter ticks or a peripheral can change the flag or interrupt line.
        if (IsStopped || Bus.SleepRequested || Bus.IrqLineAsserted || Cpu.FiqLine ||
            InstructionExecuting is not null ||
            Cpu.GetGpr(15) != 0x010e2dd4 ||
            (Cpu.CpsrValue & 0xf0000020) != 0x60000020 ||
            Cpu.GetPipelineOpcode(0) != 0x48dd ||
            Cpu.GetPipelineOpcode(1) != 0x7800 ||
            Cpu.PipelineAccess != (ArmAccess.Code | ArmAccess.Sequential) ||
            !Bus.TryGetStableTimerPoll(out uint counter, out long nextChangeCycle) ||
            Cpu.GetGpr(0) != counter || Cpu.GetGpr(3) != 16 ||
            Cpu.GetGpr(4) != counter || Cpu.GetGpr(5) != counter)
        {
            return false;
        }
        long iterations = (Math.Min(cycleLimit, nextChangeCycle - 1) - Cycles) / 23;
        if (iterations <= 0)
        {
            return false;
        }
        foreach (ArmModemInstructionObserver observer in _instructionObservers)
        {
            if (!observer.IsDisposed &&
                (observer.Address is >= 0x010e2dd0 and <= 0x010e2de2 or 0x010e2dfa))
            {
                return false;
            }
        }
        ArmModemFlash flash = Bus.Flash;
        if (!flash.TryReadCodeWord(0x010e2dd0, out uint code0) || code0 != 0x780048dd ||
            !flash.TryReadCodeWord(0x010e2dd4, out uint code1) || code1 != 0x40182310 ||
            !flash.TryReadCodeWord(0x010e2dd8, out uint code2) || code2 != 0x48dad110 ||
            !flash.TryReadCodeWord(0x010e2ddc, out uint code3) || code3 != 0x1c046800 ||
            !flash.TryReadCodeWord(0x010e2de0, out uint code4) || code4 != 0xd00a42ac ||
            !flash.TryReadCodeHalf(0x010e2dfa, out uint branch) || branch != 0xe7e9 ||
            !flash.TryReadCodeWord(0x010e3144, out uint timer) || timer != 0x00800700 ||
            !flash.TryReadCodeWord(0x010e3148, out uint flag) || flag != 0x0080095c)
        {
            return false;
        }
        Cpu.IrqLine = false;
        Bus.IdleUntilCycle(Cycles + iterations * 23);
        Instructions += iterations * 11;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void StepCore()
    {
        if (_instructionObservers.Count != 0)
        {
            DispatchInstructionObservers();
        }
        InstructionExecuting?.Invoke(this);
        bool irqLine = Bus.IrqLineAsserted;
        bool interruptAccepted = irqLine && (Cpu.CpsrValue & 0x80) == 0;
        Cpu.IrqLine = irqLine;
        if (Bus.SleepRequested && !interruptAccepted)
        {
            Bus.Idle();
            return;
        }
        if (interruptAccepted)
        {
            Bus.WakeFromInterrupt();
        }

        Cpu.Step();
        Instructions++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DispatchInstructionObservers()
    {
        uint address = CurrentInstructionAddress;
        for (var index = 0; index < _instructionObservers.Count; index++)
        {
            ArmModemInstructionObserver observer = _instructionObservers[index];
            if (!observer.IsDisposed && observer.Address == address)
            {
                observer.Callback(this);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    bool TryHandleRomFetch(uint address)
    {
        if (!ArmModemRomServices.IsEntryPoint(address))
        {
            return false;
        }

        // The intercepted instruction fetch still consumes its bus cycle.
        Bus.Idle();
        return RomServices.TryDispatch(Cpu, Bus, address);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void HandleRomCall(ArmModemRomCallException call)
    {
        try
        {
            if (RomServices.TryDispatch(Cpu, Bus, call.Address))
            {
                Instructions++;
                return;
            }

            uint instructionAddress = CurrentInstructionAddress;
            Stop(
                ArmModemStopKind.UnknownRomCall,
                $"unhandled ARM mask-ROM call address=0x{call.Address:x8} " +
                $"caller=0x{instructionAddress:x8} " +
                $"r0=0x{Cpu.GetGpr(0):x8} r1=0x{Cpu.GetGpr(1):x8} " +
                $"r2=0x{Cpu.GetGpr(2):x8} r3=0x{Cpu.GetGpr(3):x8} " +
                $"lr=0x{Cpu.GetGpr(14):x8}");
        }
        catch (ArmModemBusAccessException error)
        {
            HandleBusError(error);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void HandleBusError(ArmModemBusAccessException error)
    {
        uint instructionAddress = CurrentInstructionAddress;
        string registers =
            $"r0=0x{Cpu.GetGpr(0):x8} r1=0x{Cpu.GetGpr(1):x8} " +
            $"r2=0x{Cpu.GetGpr(2):x8} r3=0x{Cpu.GetGpr(3):x8} " +
            $"r4=0x{Cpu.GetGpr(4):x8} r5=0x{Cpu.GetGpr(5):x8} " +
            $"r6=0x{Cpu.GetGpr(6):x8} r7=0x{Cpu.GetGpr(7):x8} " +
            $"sp=0x{Cpu.GetGpr(13):x8} lr=0x{Cpu.GetGpr(14):x8}";
        int codeStart = Math.Max(0, (int)instructionAddress - 16);
        int codeLength = Math.Min(48, (int)ArmModemBus.InternalRamSize - codeStart);
        string code = instructionAddress < ArmModemBus.InternalRamSize
            ? $" ram=0x{codeStart:x}:" +
                Convert.ToHexString(Bus.SnapshotInternal((uint)codeStart, codeLength))
            : "";
        Stop(
            ArmModemStopKind.UnmappedBusAccess,
            $"{error.Message}; {registers}; flash-writes={Bus.Flash.RecentWriteSummary}{code}");
    }

    void Stop(ArmModemStopKind kind, string reason)
    {
        StopKind = kind;
        StopReason = reason;
    }

    void OnUndefinedInstructionObserved(object? sender, EventArgs e)
    {
        var cpu = (Arm7Tdmi)sender!;
        Stop(
            ArmModemStopKind.UndefinedInstruction,
            $"undefined ARM instruction pc=0x{cpu.LastUndefinedInstructionAddress:x8} " +
            $"opcode=0x{cpu.LastUndefinedInstruction:x8}");
    }
}
