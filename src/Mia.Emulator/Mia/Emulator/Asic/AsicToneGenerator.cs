// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-programmed beeper at 0x0840..0x0843 and DSP keypad-tone commands
/// at 0x08e2..0x08e9. Register
/// writes retain their effective masked value and publish immutable snapshots;
/// waveform rendering and host output remain separate from this peripheral.
/// </summary>
internal sealed class AsicToneGenerator
{
    public const int ControlAddress = 0x0840;
    public const int WidthAddress = 0x0841;
    public const int ReloadAddress = 0x0842;
    public const int UnknownAddress = 0x0843;
    public const int DspControlAddress = 0x08e2;
    public const int DspStrobeAddress = 0x08e9;

    readonly Cpu _cpu;
    readonly MiaSystemClock _clock;
    AsicToneState _currentState;
    byte _dtmfCode;

    public AsicToneGenerator(Cpu cpu, MiaSystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(clock);

        _cpu = cpu;
        _clock = clock;
        _currentState = CaptureState();

        Attach(ControlAddress);
        Attach(WidthAddress);
        Attach(ReloadAddress);
        Attach(UnknownAddress);
        for (int address = DspControlAddress; address <= DspStrobeAddress; address++)
        {
            AttachDsp(address);
        }
    }

    public AsicToneState CurrentState => _currentState;

    public event Action<AsicToneState>? StateChanged;

    void AttachDsp(int registerAddress)
    {
        var previousHook = _cpu.WriteHooks[registerAddress];
        _cpu.WriteHooks[registerAddress] = (value, oldValue, address, mask) =>
        {
            byte effective = Merge(oldValue, value, mask);
            previousHook?.Invoke(value, oldValue, address, mask);
            _cpu.Data[address] = effective;
            if (address == DspControlAddress && (effective & 0x80) == 0 ||
                address == DspStrobeAddress && effective == 0)
            {
                SetDtmfCode(0);
            }
            else if (address == DspStrobeAddress &&
                     (effective & 0x18) == 0x18 && (oldValue & 0x18) != 0x18 &&
                     (_cpu.Data[DspControlAddress] & 0xe0) == 0xe0 &&
                     _cpu.Data[0x08e4] == 0 && _cpu.Data[0x08e5] == 2 &&
                     _cpu.Data[0x08e7] == 0xc4)
            {
                // C4E1..C4EC select 1..9, 0, *, #; the remaining four
                // selections are the A..D column. Navigation keys use C4EF.
                // C4F1 stops the tone; C4E0 clears the generator.
                byte command = _cpu.Data[0x08e8];
                if (command is >= 0xe1 and <= 0xf0)
                    SetDtmfCode((byte)(command - 0xe0));
                else if (command is 0xe0 or 0xf1)
                    SetDtmfCode(0);
            }
            return true;
        };
    }

    void SetDtmfCode(byte code)
    {
        if (_dtmfCode == code) return;
        _dtmfCode = code;
        _currentState = CaptureState();
        StateChanged?.Invoke(_currentState);
    }

    void Attach(int registerAddress)
    {
        var previousHook = _cpu.WriteHooks[registerAddress];
        _cpu.WriteHooks[registerAddress] =
            (value, oldValue, address, mask) =>
            {
                var effectiveValue = Merge(oldValue, value, mask);
                if (effectiveValue != oldValue)
                {
                    _currentState = CaptureState(address, effectiveValue);
                    StateChanged?.Invoke(_currentState);
                }

                previousHook?.Invoke(value, oldValue, address, mask);
                _cpu.Data[address] = effectiveValue;
                return true;
            };
    }

    AsicToneState CaptureState(
        int overriddenAddress = -1,
        byte overriddenValue = 0)
    {
        byte Read(int address) =>
            address == overriddenAddress
                ? overriddenValue
                : _cpu.Data[address];

        return new(
            _clock.Cycles,
            Read(ControlAddress),
            Read(WidthAddress),
            Read(ReloadAddress),
            Read(UnknownAddress),
            _dtmfCode);
    }

    static byte Merge(byte oldValue, byte value, byte mask) =>
        (byte)((oldValue & ~mask) | (value & mask));
}
