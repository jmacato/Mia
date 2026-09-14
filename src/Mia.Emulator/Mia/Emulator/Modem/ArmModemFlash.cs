// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Mia.Emulator.Modem;

internal sealed class ArmModemFlash
{
    public const uint BaseAddress = 0x01000000;
    public const uint WindowSize = 0x00400000;

    readonly ArmModemFlashProfile _profile;
    readonly byte[] _data;
    readonly HashSet<uint> _writeModeBanks = [];
    readonly HashSet<uint> _bypassResetPending = [];
    readonly Queue<(uint Address, uint Value, int Size)> _recentWrites = [];
    int _identifierCommandStep;
    bool _identifierMode;
    int _unlockStep;
    uint _unlockBase;
    int _writeModeStep;
    uint _writeModeBase;
    int _eraseStep;
    uint _eraseBase;
    bool _eraseOperationActive;
    bool _eraseSuspended;
    bool _eraseTimeoutObserved;
    bool _programPending;
    uint _programAddress;

    public ArmModemFlash(ReadOnlySpan<byte> firmware, ArmModemFlashProfile profile)
    {
        if (firmware.Length > profile.Size)
        {
            throw new ArgumentException(
                $"Firmware payload is 0x{firmware.Length:x} bytes, larger than " +
                $"the configured {profile.Name} capacity 0x{profile.Size:x}.",
                nameof(firmware));
        }

        if (!IsPowerOfTwo(profile.Size))
        {
            throw new ArgumentException("Flash capacity must be a power of two.", nameof(profile));
        }

        _profile = profile;
        _data = new byte[profile.Size];
        Array.Fill(_data, byte.MaxValue);
        firmware.CopyTo(_data);
    }

    public ArmModemFlashProfile Profile => _profile;

    public string RecentWriteSummary => string.Join(' ', _recentWrites.Select(write =>
        $"{write.Size}@{write.Address:x8}={write.Value:x}"));

    public bool TryRead(uint address, int size, out uint value)
    {
        uint? result = ReadIdentifier(address, size) ??
            ReadArray(address, size);
        value = result.GetValueOrDefault();
        return result.HasValue;
    }

    uint? ReadIdentifier(uint address, int size) => address switch
    {
        0x013f0000 when _identifierMode && size == 2 =>
            _profile.ManufacturerId,
        0x013f0002 when _identifierMode && size == 2 =>
            _profile.DeviceId,
        _ => null,
    };

    uint? ReadArray(uint address, int size)
    {
        if (!Contains(address, size))
        {
            return null;
        }

        return ReadEraseStatus(address, size) ??
            ReadArrayContents(address, size);
    }

    static bool Contains(uint address, int size) =>
        address >= BaseAddress && address - BaseAddress + size <= WindowSize;

    uint? ReadEraseStatus(uint address, int size)
    {
        if (!_eraseOperationActive || (address & 0xffff0000) != _eraseBase)
        {
            return null;
        }

        if (_eraseSuspended)
        {
            return RepeatByte(0x40, size);
        }

        return ReadActiveEraseStatus(size);
    }

    uint? ReadActiveEraseStatus(int size)
    {
        if (!_eraseTimeoutObserved)
        {
            _eraseTimeoutObserved = true;
            return RepeatByte(0x08, size);
        }

        CompleteErase();
        return null;
    }

    uint? ReadArrayContents(uint address, int size)
    {
        uint physicalOffset = (address - BaseAddress) & (_profile.Size - 1);
        if (physicalOffset + size > _profile.Size)
        {
            return null;
        }

        return size switch
        {
            4 => BinaryPrimitives.ReadUInt32LittleEndian(
                _data.AsSpan((int)physicalOffset, 4)),
            2 => BinaryPrimitives.ReadUInt16LittleEndian(
                _data.AsSpan((int)physicalOffset, 2)),
            _ => _data[physicalOffset],
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadCodeHalf(uint address, out uint value)
    {
        uint windowOffset = address - BaseAddress;
        if (windowOffset > WindowSize - sizeof(ushort) ||
            (_identifierMode && address is 0x013f0000 or 0x013f0002) ||
            (_eraseOperationActive && (address & 0xffff0000) == _eraseBase))
        {
            value = 0;
            return false;
        }

        uint physicalOffset = windowOffset & (_profile.Size - 1);
        value = BinaryPrimitives.ReadUInt16LittleEndian(
            _data.AsSpan((int)physicalOffset, sizeof(ushort)));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadCodeWord(uint address, out uint value)
    {
        uint windowOffset = address - BaseAddress;
        if (windowOffset > WindowSize - sizeof(uint) ||
            (_eraseOperationActive && (address & 0xffff0000) == _eraseBase))
        {
            value = 0;
            return false;
        }

        uint physicalOffset = windowOffset & (_profile.Size - 1);
        value = BinaryPrimitives.ReadUInt32LittleEndian(
            _data.AsSpan((int)physicalOffset, sizeof(uint)));
        return true;
    }

    public bool TryWrite(uint address, uint value, int size)
    {
        var command = new ArmModemFlashCommand(address, value, size);
        RecordRecentWrite(command);
        return TryChangeEraseState(command) ||
            TryRunIdentifierCommand(command) ||
            TryRunUnlockCommand(command) ||
            TryExitWriteMode(command) ||
            TryRunEraseCommand(command) ||
            TryEnterWriteMode(command) ||
            TryProgram(command);
    }

    void RecordRecentWrite(ArmModemFlashCommand command)
    {
        if (command.Address < BaseAddress ||
            command.Address - BaseAddress >= WindowSize)
        {
            return;
        }
        _recentWrites.Enqueue((
            command.Address,
            command.Value,
            command.Size));
        TrimRecentWrites();
    }

    void TrimRecentWrites()
    {
        while (_recentWrites.Count > 16)
        {
            _recentWrites.Dequeue();
        }
    }

    bool TryChangeEraseState(ArmModemFlashCommand command)
    {
        if (command.Size == 1 && _eraseOperationActive &&
            command.BankBase == _eraseBase && command.Value == 0xb0)
        {
            _eraseSuspended = true;
            return true;
        }
        if (command.Size == 1 && _eraseOperationActive && _eraseSuspended &&
            command.BankBase == _eraseBase && command.Value == 0x30)
        {
            _eraseSuspended = false;
            return true;
        }
        return false;
    }

    bool TryRunIdentifierCommand(ArmModemFlashCommand command)
    {
        if (command.Size != 1)
        {
            return false;
        }
        if (_identifierMode && command.Address == 0x013f0000 &&
            command.Value == 0xf0)
        {
            _identifierMode = false;
            _identifierCommandStep = 0;
            return true;
        }
        return _identifierCommandStep switch
        {
            0 => TryStartIdentifierCommand(command),
            1 => TryContinueIdentifierCommand(command),
            2 => TryCompleteIdentifierCommand(command),
            _ => false,
        };
    }

    bool TryStartIdentifierCommand(ArmModemFlashCommand command)
    {
        if (command.Address != 0x013f0aaa || command.Value != 0xaa)
        {
            return false;
        }
        _identifierCommandStep = 1;
        return true;
    }

    bool TryContinueIdentifierCommand(ArmModemFlashCommand command)
    {
        if (command.Address != 0x013f0555 || command.Value != 0x55)
        {
            return false;
        }
        _identifierCommandStep = 2;
        return true;
    }

    bool TryCompleteIdentifierCommand(ArmModemFlashCommand command)
    {
        if (command.Address != 0x013f0aaa || command.Value != 0x90)
        {
            return false;
        }
        _identifierMode = true;
        _identifierCommandStep = 0;
        return true;
    }

    bool TryRunUnlockCommand(ArmModemFlashCommand command)
    {
        if (command.Size != 2)
        {
            return false;
        }
        return _unlockStep switch
        {
            0 => TryStartUnlock(command),
            1 => TryContinueUnlock(command),
            2 => TrySelectUnlock(command),
            3 => TryCompleteUnlock(command),
            _ => false,
        };
    }

    bool TryStartUnlock(ArmModemFlashCommand command)
    {
        if (command.Value != 0xaa ||
            command.Address is < 0x011c0aaa or > 0x011f0aaa ||
            (command.Address & 0xffff) != 0x0aaa)
        {
            return false;
        }
        _unlockBase = command.Address - 0x0aaa;
        _unlockStep = 1;
        return true;
    }

    bool TryContinueUnlock(ArmModemFlashCommand command)
    {
        if (command.Address != _unlockBase + 0x0554 || command.Value != 0x55)
        {
            return false;
        }
        _unlockStep = 2;
        return true;
    }

    bool TrySelectUnlock(ArmModemFlashCommand command)
    {
        if (command.Address != _unlockBase + 0x0aaa || command.Value != 0x60)
        {
            return false;
        }
        _unlockStep = 3;
        return true;
    }

    bool TryCompleteUnlock(ArmModemFlashCommand command)
    {
        if (command.Address != _unlockBase || command.Value != 0xd0)
        {
            return false;
        }
        _unlockStep = 0;
        return true;
    }

    bool TryExitWriteMode(ArmModemFlashCommand command)
    {
        if (command.Size == 1 && command.IsParameterBank &&
            _writeModeBanks.Contains(command.BankBase) &&
            command.Address == command.BankBase && command.Value == 0x90)
        {
            _bypassResetPending.Add(command.BankBase);
            return true;
        }
        if (command.Size == 1 && command.IsParameterBank &&
            _bypassResetPending.Contains(command.BankBase) &&
            command.Address == command.BankBase && command.Value == 0)
        {
            _bypassResetPending.Remove(command.BankBase);
            _writeModeBanks.Remove(command.BankBase);
            return true;
        }
        return false;
    }

    bool TryRunEraseCommand(ArmModemFlashCommand command)
    {
        if (command.Size != 1)
        {
            return false;
        }
        return _eraseStep switch
        {
            1 => TryContinueErase(command),
            2 => TrySelectErase(command),
            3 => TryStartErase(command),
            _ => false,
        };
    }

    bool TryContinueErase(ArmModemFlashCommand command)
    {
        if (command.Address != _eraseBase + 0x0aaa || command.Value != 0xaa)
        {
            return false;
        }
        _eraseStep = 2;
        return true;
    }

    bool TrySelectErase(ArmModemFlashCommand command)
    {
        if (command.Address != _eraseBase + 0x0554 || command.Value != 0x55)
        {
            return false;
        }
        _eraseStep = 3;
        return true;
    }

    bool TryStartErase(ArmModemFlashCommand command)
    {
        if (command.BankBase != _eraseBase || command.Value != 0x30)
        {
            return false;
        }
        _eraseStep = 0;
        _eraseOperationActive = true;
        _eraseSuspended = false;
        _eraseTimeoutObserved = false;
        return true;
    }

    bool TryEnterWriteMode(ArmModemFlashCommand command)
    {
        if (command.Size != 1)
        {
            return false;
        }
        return _writeModeStep switch
        {
            0 => TryStartWriteMode(command),
            1 => TryContinueWriteMode(command),
            2 => TryCompleteWriteMode(command),
            _ => false,
        };
    }

    bool TryStartWriteMode(ArmModemFlashCommand command)
    {
        if (!command.IsParameterBank ||
            command.Address != command.BankBase + 0x0aaa ||
            command.Value != 0xaa)
        {
            return false;
        }
        _writeModeBase = command.BankBase;
        _writeModeStep = 1;
        return true;
    }

    bool TryContinueWriteMode(ArmModemFlashCommand command)
    {
        if (command.Address != _writeModeBase + 0x0554 || command.Value != 0x55)
        {
            return false;
        }
        _writeModeStep = 2;
        return true;
    }

    bool TryCompleteWriteMode(ArmModemFlashCommand command)
    {
        if (command.Address != _writeModeBase + 0x0aaa)
        {
            return false;
        }
        if (command.Value == 0x20)
        {
            _writeModeBanks.Add(_writeModeBase);
            _writeModeStep = 0;
            return true;
        }
        if (command.Value != 0x80)
        {
            return false;
        }
        _eraseBase = _writeModeBase;
        _eraseStep = 1;
        _writeModeStep = 0;
        return true;
    }

    bool TryProgram(ArmModemFlashCommand command)
    {
        if (command.Size != 2)
        {
            return false;
        }
        if (_programPending)
        {
            return CompleteProgram(command);
        }
        if (!command.IsParameterBank ||
            !_writeModeBanks.Contains(command.BankBase) ||
            command.Value != 0xa0)
        {
            return false;
        }
        _programAddress = command.Address;
        _programPending = true;
        return true;
    }

    bool CompleteProgram(ArmModemFlashCommand command)
    {
        if (command.Address != _programAddress)
        {
            return false;
        }
        int physicalOffset = (int)(
            (command.Address - BaseAddress) & (_profile.Size - 1));
        ushort existing = BinaryPrimitives.ReadUInt16LittleEndian(
            _data.AsSpan(physicalOffset, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(
            _data.AsSpan(physicalOffset, 2),
            (ushort)(existing & command.Value));
        _programPending = false;
        return true;
    }

    void CompleteErase()
    {
        int physicalOffset = (int)((_eraseBase - BaseAddress) & (_profile.Size - 1));
        Array.Fill(_data, byte.MaxValue, physicalOffset, 0x10000);
        _eraseOperationActive = false;
        _eraseSuspended = false;
    }

    static uint RepeatByte(byte value, int size) => size switch
    {
        4 => value * 0x01010101u,
        2 => value * 0x0101u,
        _ => value,
    };

    static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
}
