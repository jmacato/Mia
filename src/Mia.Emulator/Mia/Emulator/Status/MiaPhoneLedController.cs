// SPDX-License-Identifier: MIT

using AvrCore;
using AvrCore.Execution;

namespace Mia.Emulator.Status;

/// <summary>
/// Projects the recovered firmware-visible light controls onto the handset's
/// physical red/green and blue lamp packages and paired illumination rails.
/// </summary>
internal sealed class MiaPhoneLedController : IDisposable
{
    public const int SharedControlAddress = 0x0a40;
    public const int BlueControlAddress = SharedControlAddress;
    public const int GreenControlAddress = 0x0a44;
    public const byte BlueMask = 0x08;
    public const byte IlluminationBit4Mask = 0x10;
    public const byte IlluminationBit1Mask = 0x02;
    public const byte GreenMask = 0x01;
    public const byte RedMask = 0x80;

    const int LeftRedStateBit = 1 << 0;
    const int LeftGreenStateBit = 1 << 1;
    const int RightBlueStateBit = 1 << 2;
    const int IlluminationBit4StateBit = 1 << 3;
    const int IlluminationBit1StateBit = 1 << 4;

    readonly Cpu _cpu;
    readonly MiaPowerPortController _powerPorts;
    readonly CpuMemoryWriteHook? _previousSharedWriteHook;
    readonly CpuMemoryWriteHook? _previousGreenWriteHook;
    readonly CpuMemoryWriteHook _sharedWriteHook;
    readonly CpuMemoryWriteHook _greenWriteHook;
    int _stateBits;
    int _disposed;

    public MiaPhoneLedController(
        Cpu cpu,
        MiaPowerPortController powerPorts)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(powerPorts);
        _cpu = cpu;
        _powerPorts = powerPorts;
        if ((powerPorts.OutputState.PortA4 & RedMask) != 0)
        {
            _stateBits = LeftRedStateBit;
        }

        _previousSharedWriteHook = cpu.WriteHooks[SharedControlAddress];
        _previousGreenWriteHook = cpu.WriteHooks[GreenControlAddress];
        _sharedWriteHook = WriteSharedControl;
        _greenWriteHook = WriteGreenControl;
        cpu.WriteHooks[SharedControlAddress] = _sharedWriteHook;
        cpu.WriteHooks[GreenControlAddress] = _greenWriteHook;
        powerPorts.OutputStateChanged += ObservePowerPortOutputs;
    }

    public MiaPhoneLedState State => DecodeState(
        Volatile.Read(ref _stateBits));

    public event Action<MiaPhoneLedState>? StateChanged;

    bool WriteSharedControl(byte value, byte oldValue, int address, byte mask)
    {
        bool consumed = _previousSharedWriteHook?.Invoke(
            value,
            oldValue,
            address,
            mask) ?? false;
        if (!consumed)
        {
            byte effectiveValue = ApplyMask(value, oldValue, mask);
            SetState(new MiaPhoneLedUpdate(
                RightBlue: (effectiveValue & BlueMask) != 0,
                IlluminationBit4:
                    (effectiveValue & IlluminationBit4Mask) != 0,
                IlluminationBit1:
                    (effectiveValue & IlluminationBit1Mask) != 0));
        }
        return consumed;
    }

    bool WriteGreenControl(byte value, byte oldValue, int address, byte mask)
    {
        bool consumed = _previousGreenWriteHook?.Invoke(
            value,
            oldValue,
            address,
            mask) ?? false;
        if (!consumed)
        {
            byte effectiveValue = ApplyMask(value, oldValue, mask);
            SetState(new MiaPhoneLedUpdate(
                LeftGreen: (effectiveValue & GreenMask) == 0));
        }
        return consumed;
    }

    void ObservePowerPortOutputs(MiaPowerPortOutputState outputs) =>
        SetState(new MiaPhoneLedUpdate(
            LeftRed: (outputs.PortA4 & RedMask) != 0));

    void SetState(MiaPhoneLedUpdate update)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            int previous = Volatile.Read(ref _stateBits);
            int next = SetBit(previous, LeftRedStateBit, update.LeftRed);
            next = SetBit(next, LeftGreenStateBit, update.LeftGreen);
            next = SetBit(next, RightBlueStateBit, update.RightBlue);
            next = SetBit(
                next,
                IlluminationBit4StateBit,
                update.IlluminationBit4);
            next = SetBit(
                next,
                IlluminationBit1StateBit,
                update.IlluminationBit1);
            if (next == previous)
            {
                return;
            }
            if (Interlocked.CompareExchange(
                ref _stateBits,
                next,
                previous) == previous)
            {
                StateChanged?.Invoke(DecodeState(next));
                return;
            }
        }
    }

    static byte ApplyMask(byte value, byte oldValue, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));

    static int SetBit(int bits, int bit, bool? value) => value switch
    {
        true => bits | bit,
        false => bits & ~bit,
        null => bits,
    };

    static MiaPhoneLedState DecodeState(int bits) => new(
        (bits & LeftRedStateBit) != 0,
        (bits & LeftGreenStateBit) != 0,
        (bits & RightBlueStateBit) != 0,
        (bits & IlluminationBit4StateBit) != 0,
        (bits & IlluminationBit1StateBit) != 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StateChanged = null;
        _powerPorts.OutputStateChanged -= ObservePowerPortOutputs;
        if (ReferenceEquals(
            _cpu.WriteHooks[SharedControlAddress],
            _sharedWriteHook))
        {
            _cpu.WriteHooks[SharedControlAddress] = _previousSharedWriteHook;
        }
        if (ReferenceEquals(
            _cpu.WriteHooks[GreenControlAddress],
            _greenWriteHook))
        {
            _cpu.WriteHooks[GreenControlAddress] = _previousGreenWriteHook;
        }
    }
}
