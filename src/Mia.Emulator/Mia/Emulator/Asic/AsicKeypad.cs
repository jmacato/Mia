// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Minimal active-low keypad matrix boundary proven by the firmware scanner.
/// </summary>
internal sealed class AsicKeypad
{
    public const int RowStateAddress = 0x0a4f;
    public const int ScanControlAddress = 0x0a50;
    public const byte InterruptEnable = 0x10;
    public const byte IdleRows = 0x1f;
    public const byte NoPowerScanMask = 0x0f;
    public const byte NoPowerRowMask = 0x01;
    public const byte SideRockerScanMask = 0x0d;
    public const byte SideRockerUpperRowMask = 0x04;
    public const byte SideRockerLowerRowMask = 0x08;

    readonly Cpu _cpu;
    readonly int[] _configuredContacts;
    readonly Dictionary<int, int> _pressedContacts = [];

    public AsicKeypad(
        Cpu cpu,
        byte? pressedScanMask = null,
        byte? secondaryPressedScanMask = null,
        byte pressedRowMask = 0,
        bool pressedInitially = true)
    {
        _cpu = cpu;
        _configuredContacts = pressedScanMask is byte scanMask
            ? secondaryPressedScanMask is byte secondaryScanMask
                ? [
                    EncodeContact(scanMask, pressedRowMask),
                    EncodeContact(secondaryScanMask, pressedRowMask),
                ]
                : [EncodeContact(scanMask, pressedRowMask)]
            : [];
        if (pressedInitially)
        {
            Press();
        }
        cpu.ReadHooks[RowStateAddress] = ReadRows;
        cpu.WriteHooks[ScanControlAddress] = WriteScanControl;
    }

    /// <summary>
    /// Raised when a firmware MMIO read actually observes a grounded contact.
    /// </summary>
    public event Action<byte, byte>? ContactObserved;

    /// <summary>
    /// Raised after one row read has reported every observed contact, allowing
    /// host-input state to commit deferred edges without instruction polling.
    /// </summary>
    public event Action? ScanCompleted;

    public event Action? InterruptRequested;

    bool WriteScanControl(byte value, byte oldValue, int address, byte mask)
    {
        byte effective = (byte)((oldValue & ~mask) | (value & mask));
        _cpu.Data[address] = effective;
        // Arming key detection with a contact already held must wake the
        // scanner too. This is how the initial power-key hold reaches the
        // firmware; there need not be another host key-down edge after boot.
        if ((effective & InterruptEnable) != 0 &&
            (oldValue & InterruptEnable) == 0 && _pressedContacts.Count != 0)
        {
            InterruptRequested?.Invoke();
        }
        return true;
    }

    public void Press()
    {
        foreach (var contact in _configuredContacts)
        {
            PressContact(contact);
        }
    }

    public void Release()
    {
        foreach (var contact in _configuredContacts)
        {
            ReleaseContact(contact);
        }
    }

    public void Press(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask = null)
    {
        PressContact(EncodeContact(scanMask, rowMask));
        if (secondaryScanMask is byte secondary)
        {
            PressContact(EncodeContact(secondary, rowMask));
        }
    }

    public void Release(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask = null)
    {
        ReleaseContact(EncodeContact(scanMask, rowMask));
        if (secondaryScanMask is byte secondary)
        {
            ReleaseContact(EncodeContact(secondary, rowMask));
        }
    }

    public static bool IsValidContact(byte scanMask, byte rowMask) =>
        scanMask is 0x07 or 0x0b or 0x0d or 0x0e or 0x0f &&
        rowMask is 0x01 or 0x02 or 0x04 or 0x08 or 0x10;

    byte ReadRows(int _)
    {
        var selectedScanMask = (byte)(_cpu.Data[ScanControlAddress] & 0x0f);
        var rows = IdleRows;
        foreach (var contact in _pressedContacts.Keys)
        {
            if ((byte)(contact >> 8) == selectedScanMask)
            {
                var rowMask = (byte)contact;
                rows &= unchecked((byte)~rowMask);
                ContactObserved?.Invoke(selectedScanMask, rowMask);
            }
        }
        ScanCompleted?.Invoke();
        return rows;
    }

    void PressContact(int contact) =>
        _pressedContacts[contact] = _pressedContacts.GetValueOrDefault(contact) + 1;

    void ReleaseContact(int contact)
    {
        if (!_pressedContacts.TryGetValue(contact, out var count))
        {
            return;
        }
        if (count == 1)
        {
            _pressedContacts.Remove(contact);
        }
        else
        {
            _pressedContacts[contact] = count - 1;
        }
    }

    static int EncodeContact(byte scanMask, byte rowMask)
    {
        if (!IsValidContact(scanMask, rowMask))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scanMask),
                $"Invalid keypad contact scan=0x{scanMask:x2}, row=0x{rowMask:x2}.");
        }
        return scanMask << 8 | rowMask;
    }
}
