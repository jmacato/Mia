// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed class PrimaryI2cTraceDecoder
{
    public const int DataAddress = 0x0839;
    public const int ControlAddress = DataAddress + 1;

    readonly Action<long, int, bool, byte, byte, byte, int> _completed;
    readonly Dictionary<byte, byte> _registerPointers = [];
    bool _started;
    bool _reading;
    bool _hasPendingData;
    bool _readBytePending;
    byte _pendingData;
    long _pendingCycle;
    int _pendingPc;
    byte? _address;
    byte? _combinedDevice;
    byte? _combinedRegister;
    int _writeByteCount;
    byte _firstWriteByte;
    int _readByteCount;

    public PrimaryI2cTraceDecoder(
        Action<long, int, bool, byte, byte, byte, int> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        _completed = completed;
    }

    public void ObserveRead(
        long cycle,
        int pc,
        int address,
        byte value)
    {
        if (address == DataAddress && _started && _reading && _readBytePending)
        {
            byte device = _combinedDevice ??
                (byte)(_address.GetValueOrDefault() & 0xfe);
            byte register = _combinedRegister.HasValue
                ? (byte)(_combinedRegister.Value + _readByteCount)
                : _registerPointers.GetValueOrDefault(device);
            _completed(cycle, pc, false, device, register, value, 1);
            _readByteCount++;
            _registerPointers[device] = (byte)(register + 1);
            _readBytePending = false;
        }
    }

    public void ObserveWrite(
        long cycle,
        int pc,
        int address,
        byte value)
    {
        if (address == DataAddress)
        {
            _pendingData = value;
            _pendingCycle = cycle;
            _pendingPc = pc;
            _hasPendingData = true;
            return;
        }
        if (address != ControlAddress)
        {
            return;
        }

        switch (value)
        {
            case 0xa0:
                StartOrRepeat();
                break;
            case 0x80:
                ContinueTransfer(cycle, pc);
                break;
            case 0x84:
                if (_started && _reading && _address.HasValue)
                {
                    _readBytePending = true;
                }
                break;
            case 0x98:
                Complete(cycle, pc);
                break;
        }
    }

    void StartOrRepeat()
    {
        if (!_started)
        {
            ResetLogicalTransaction();
            _started = true;
            return;
        }

        CaptureCombinedWritePhase();
        _address = null;
        _reading = false;
        _hasPendingData = false;
        _readBytePending = false;
        ResetPhaseBytes();
    }

    void ContinueTransfer(long cycle, int pc)
    {
        if (!_started)
        {
            return;
        }
        if (!_address.HasValue)
        {
            AcceptAddress();
            return;
        }
        if (_reading)
        {
            _readBytePending = true;
            return;
        }
        ContinueWrite();
    }

    void AcceptAddress()
    {
        if (!_hasPendingData)
        {
            return;
        }
        _address = _pendingData;
        _reading = (_pendingData & 1) != 0;
        _hasPendingData = false;
    }

    void ContinueWrite()
    {
        if (!_hasPendingData)
        {
            return;
        }
        if (_writeByteCount == 0)
        {
            _firstWriteByte = _pendingData;
        }
        else
        {
            RecordWriteByte();
        }
        _writeByteCount++;
        _hasPendingData = false;
    }

    void RecordWriteByte()
    {
        byte device = (byte)(_address.GetValueOrDefault() & 0xfe);
        byte register = (byte)(_firstWriteByte + _writeByteCount - 1);
        _completed(
            _pendingCycle,
            _pendingPc,
            true,
            device,
            register,
            _pendingData,
            1);
        _registerPointers[device] = (byte)(register + 1);
    }

    void CaptureCombinedWritePhase()
    {
        if (!_address.HasValue || _reading)
        {
            return;
        }

        byte device = (byte)(_address.Value & 0xfe);
        _combinedDevice = device;
        if (_writeByteCount != 0)
        {
            _combinedRegister = _firstWriteByte;
            _registerPointers[device] = _firstWriteByte;
        }
    }

    void Complete(long cycle, int pc)
    {
        if (!_started || !_address.HasValue)
        {
            ResetLogicalTransaction();
            return;
        }

        byte device = (byte)(_address.Value & 0xfe);
        if (!_reading)
        {
            CompleteWrite(cycle, pc, device);
        }
        else if (_readByteCount == 0)
        {
            CompleteEmptyRead(cycle, pc, device);
        }
        ResetLogicalTransaction();
    }

    void CompleteWrite(long cycle, int pc, byte device)
    {
        byte register = _writeByteCount != 0
            ? _firstWriteByte
            : _registerPointers.GetValueOrDefault(device);
        if (_writeByteCount <= 1)
        {
            _completed(cycle, pc, true, device, register, 0, 0);
        }
    }

    void CompleteEmptyRead(long cycle, int pc, byte device)
    {
        device = _combinedDevice ?? device;
        byte register = _combinedRegister ??
            _registerPointers.GetValueOrDefault(device);
        _completed(cycle, pc, false, device, register, 0, 0);
    }

    void ResetLogicalTransaction()
    {
        _started = false;
        _reading = false;
        _hasPendingData = false;
        _readBytePending = false;
        _pendingCycle = 0;
        _pendingPc = 0;
        _address = null;
        _combinedDevice = null;
        _combinedRegister = null;
        ResetPhaseBytes();
    }

    void ResetPhaseBytes()
    {
        _writeByteCount = 0;
        _firstWriteByte = 0;
        _readByteCount = 0;
    }
}
