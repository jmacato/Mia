// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

using System.Runtime.CompilerServices;

namespace Arm7Core;

public sealed partial class Arm7Tdmi
{
    void ExecuteThumbMoveShifted(ushort instruction)
    {
        int operation = (int)Bits(instruction, 11, 2);
        uint amount = Bits(instruction, 6, 5);
        int source = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        Arm7TdmiShiftResult shifted = Shift(new(
            ReadRegister(source),
            operation,
            amount,
            RegisterSpecified: false));
        WriteRegister(destination, shifted.Value);
        SetNegativeAndZero(shifted.Value);
        if (shifted.Carry.HasValue)
        {
            SetFlag(FlagC, shifted.Carry.Value);
        }
    }

    void ExecuteThumbAddSubtract(ushort instruction)
    {
        bool immediate = Bits(instruction, 10, 1) != 0;
        bool subtract = Bits(instruction, 9, 1) != 0;
        int operand = (int)Bits(instruction, 6, 3);
        int source = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint left = ReadRegister(source);
        uint right = immediate ? (uint)operand : ReadRegister(operand);
        Arm7TdmiAluResult result = subtract
            ? AddWithCarry(left, ~right, true)
            : AddWithCarry(left, right, false);
        WriteRegister(destination, result.Value);
        SetArithmeticFlags(result.Value, result.Carry, result.Overflow);
    }

    void ExecuteThumbImmediateAlu(ushort instruction)
    {
        int operation = (int)Bits(instruction, 11, 2);
        int destination = (int)Bits(instruction, 8, 3);
        uint immediate = Bits(instruction, 0, 8);
        uint left = ReadRegister(destination);
        uint result;

        switch (operation)
        {
            case 0:
                result = immediate;
                WriteRegister(destination, result);
                SetNegativeAndZero(result);
                return;
            case 1:
                Arm7TdmiAluResult comparison = AddWithCarry(
                    left,
                    ~immediate,
                    true);
                SetArithmeticFlags(
                    comparison.Value,
                    comparison.Carry,
                    comparison.Overflow);
                return;
            case 2:
                Arm7TdmiAluResult addition = AddWithCarry(
                    left,
                    immediate,
                    false);
                WriteRegister(destination, addition.Value);
                SetArithmeticFlags(
                    addition.Value,
                    addition.Carry,
                    addition.Overflow);
                return;
            default:
                Arm7TdmiAluResult subtraction = AddWithCarry(
                    left,
                    ~immediate,
                    true);
                WriteRegister(destination, subtraction.Value);
                SetArithmeticFlags(
                    subtraction.Value,
                    subtraction.Carry,
                    subtraction.Overflow);
                return;
        }
    }

    void ExecuteThumbAlu(ushort instruction)
    {
        int operation = (int)Bits(instruction, 6, 4);
        int source = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint left = ReadRegister(destination);
        uint right = ReadRegister(source);
        switch (operation)
        {
            case 0:
                WriteLogicalResult(destination, left & right);
                break;
            case 1:
                WriteLogicalResult(destination, left ^ right);
                break;
            case 2:
            case 3:
            case 4:
            case 7:
                ExecuteThumbRegisterShift(operation, destination, left, right);
                break;
            case 5:
                WriteThumbArithmeticResult(
                    destination,
                    AddWithCarry(left, right, Flag(FlagC)));
                break;
            case 6:
                WriteThumbArithmeticResult(
                    destination,
                    AddWithCarry(left, ~right, Flag(FlagC)));
                break;
            case 8:
                SetNegativeAndZero(left & right);
                break;
            case 9:
                WriteThumbArithmeticResult(
                    destination,
                    AddWithCarry(0, ~right, true));
                break;
            case 10:
                SetThumbArithmeticFlags(AddWithCarry(left, ~right, true));
                break;
            case 11:
                SetThumbArithmeticFlags(AddWithCarry(left, right, false));
                break;
            case 12:
                WriteLogicalResult(destination, left | right);
                break;
            case 13:
                ExecuteThumbMultiply(destination, left, right);
                break;
            case 14:
                WriteLogicalResult(destination, left & ~right);
                break;
            default:
                WriteLogicalResult(destination, ~right);
                break;
        }
    }

    void ExecuteThumbRegisterShift(
        int operation,
        int destination,
        uint value,
        uint amount)
    {
        Idle();
        int shiftType = operation == 7 ? 3 : operation - 2;
        Arm7TdmiShiftResult shifted = Shift(new(
            value,
            shiftType,
            amount & 0xffu,
            RegisterSpecified: true));
        WriteRegister(destination, shifted.Value);
        SetNegativeAndZero(shifted.Value);
        if (shifted.Carry.HasValue)
        {
            SetFlag(FlagC, shifted.Carry.Value);
        }
    }

    void WriteThumbArithmeticResult(
        int destination,
        Arm7TdmiAluResult result)
    {
        WriteRegister(destination, result.Value);
        SetThumbArithmeticFlags(result);
    }

    void SetThumbArithmeticFlags(Arm7TdmiAluResult result) =>
        SetArithmeticFlags(result.Value, result.Carry, result.Overflow);

    void ExecuteThumbMultiply(int destination, uint left, uint right)
    {
        IdleMultiply(left, signed: true, longMultiply: false);
        Arm7TdmiMultiplyResult multiplication = BoothMultiply(
            Arm7TdmiMultiplyFlavor.Short,
            right,
            left,
            0);
        uint result = (uint)multiplication.Value;
        WriteRegister(destination, result);
        SetNegativeAndZero(result);
        SetFlag(FlagC, multiplication.Carry);
    }

    void ExecuteThumbHighRegister(ushort instruction)
    {
        int operation = (int)Bits(instruction, 8, 2);
        int source = (int)Bits(instruction, 3, 3) + (int)(Bits(instruction, 6, 1) << 3);
        int destination = (int)Bits(instruction, 0, 3) + (int)(Bits(instruction, 7, 1) << 3);

        if (operation == 3)
        {
            uint target = ReadRegister(source);
            IsThumb = (target & 1u) != 0;
            BranchTo(IsThumb ? target & ~1u : target);
            return;
        }

        uint right = ReadRegister(source);
        switch (operation)
        {
            case 0:
                WriteRegister(
                    destination,
                    unchecked(ReadRegister(destination) + right));
                break;
            case 1:
                SetThumbArithmeticFlags(AddWithCarry(
                    ReadRegister(destination),
                    ~right,
                    true));
                break;
            default:
                WriteRegister(destination, right);
                break;
        }
    }

    void ExecuteThumbPcRelativeLoad(ushort instruction)
    {
        int destination = (int)Bits(instruction, 8, 3);
        uint address = (_registers[ProgramCounter] & ~3u) + Bits(instruction, 0, 8) * 4u;
        BreakSequentialFetch();
        uint value = ReadWord(address);
        Idle();
        WriteRegister(destination, value);
    }

    void ExecuteThumbRegisterTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        bool byteTransfer = Bits(instruction, 10, 1) != 0;
        int offsetRegister = (int)Bits(instruction, 6, 3);
        int baseRegister = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint address = ReadRegister(baseRegister) + ReadRegister(offsetRegister);
        BreakSequentialFetch();

        ExecuteThumbWordOrByteTransfer(
            load,
            byteTransfer,
            address,
            destination);
    }

    void ExecuteThumbSignedTransfer(ushort instruction)
    {
        int operation = (int)Bits(instruction, 10, 2);
        int offsetRegister = (int)Bits(instruction, 6, 3);
        int baseRegister = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint address = ReadRegister(baseRegister) + ReadRegister(offsetRegister);
        BreakSequentialFetch();

        switch (operation)
        {
            case 0:
                WriteHalf(address, (ushort)ReadRegister(destination));
                return;
            case 1:
                {
                    uint value = ExtendSignBit(ReadByte(address), 0x80u);
                    Idle();
                    WriteRegister(destination, value);
                    return;
                }
            case 2:
                {
                    uint value = ReadRotatedHalf(address);
                    Idle();
                    WriteRegister(destination, value);
                    return;
                }
            default:
                {
                    uint raw = ReadHalf(address);
                    uint value = (address & 1u) != 0
                        ? ExtendSignBit((raw >> 8) & 0xffu, 0x80u)
                        : ExtendSignBit(raw, 0x8000u);
                    Idle();
                    WriteRegister(destination, value);
                    return;
                }
        }
    }

    void ExecuteThumbImmediateTransfer(ushort instruction)
    {
        bool byteTransfer = Bits(instruction, 12, 1) != 0;
        bool load = Bits(instruction, 11, 1) != 0;
        uint offset = Bits(instruction, 6, 5);
        int baseRegister = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint address = ReadRegister(baseRegister) + (byteTransfer ? offset : offset * 4u);
        BreakSequentialFetch();

        ExecuteThumbWordOrByteTransfer(
            load,
            byteTransfer,
            address,
            destination);
    }

    void ExecuteThumbWordOrByteTransfer(
        bool load,
        bool byteTransfer,
        uint address,
        int register)
    {
        if (load)
        {
            uint value = byteTransfer ? ReadByte(address) : ReadRotatedWord(address);
            Idle();
            WriteRegister(register, value);
            return;
        }
        if (byteTransfer)
        {
            WriteByte(address, (byte)ReadRegister(register));
            return;
        }

        WriteWord(address, ReadRegister(register));
    }

    void ExecuteThumbImmediateHalfwordTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        uint address = ReadRegister((int)Bits(instruction, 3, 3)) + Bits(instruction, 6, 5) * 2u;
        int destination = (int)Bits(instruction, 0, 3);
        BreakSequentialFetch();
        if (load)
        {
            uint value = ReadRotatedHalf(address);
            Idle();
            WriteRegister(destination, value);
        }
        else
        {
            WriteHalf(address, (ushort)ReadRegister(destination));
        }
    }

    void ExecuteThumbSpRelativeTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        int destination = (int)Bits(instruction, 8, 3);
        uint address = ReadRegister(13) + Bits(instruction, 0, 8) * 4u;
        BreakSequentialFetch();
        if (load)
        {
            uint value = ReadRotatedWord(address);
            Idle();
            WriteRegister(destination, value);
        }
        else
        {
            WriteWord(address, ReadRegister(destination));
        }
    }

    void ExecuteThumbLoadAddress(ushort instruction)
    {
        bool fromStack = Bits(instruction, 11, 1) != 0;
        int destination = (int)Bits(instruction, 8, 3);
        uint value = fromStack ? ReadRegister(13) : _registers[ProgramCounter] & ~3u;
        WriteRegister(destination, value + Bits(instruction, 0, 8) * 4u);
    }

    void ExecuteThumbAddSpOffset(ushort instruction)
    {
        uint offset = Bits(instruction, 0, 7) * 4u;
        _registers[RegisterIndex(13)] = Bits(instruction, 7, 1) != 0
            ? unchecked(ReadRegister(13) - offset)
            : unchecked(ReadRegister(13) + offset);
    }

    void ExecuteThumbPushPop(ushort instruction)
    {
        bool pop = Bits(instruction, 11, 1) != 0;
        bool includeHighRegister = Bits(instruction, 8, 1) != 0;
        uint registerList = Bits(instruction, 0, 8);
        if (includeHighRegister)
        {
            registerList |= pop ? 1u << ProgramCounter : 1u << 14;
        }
        uint armInstruction = 0xe8000000u |
            (pop ? 1u << 23 : 1u << 24) |
            1u << 21 |
            (pop ? 1u << 20 : 0u) |
            13u << 16 |
            registerList;
        ExecuteArmBlockTransfer(armInstruction);
    }

    void ExecuteThumbMultipleTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        uint armInstruction = 0xe8000000u |
            1u << 23 |
            1u << 21 |
            (load ? 1u << 20 : 0u) |
            Bits(instruction, 8, 3) << 16 |
            Bits(instruction, 0, 8);
        ExecuteArmBlockTransfer(armInstruction);
    }

    void ExecuteThumbConditionalBranch(ushort instruction)
    {
        uint condition = Bits(instruction, 8, 4);
        if (CheckCondition(condition))
        {
            int offset = SignExtend(Bits(instruction, 0, 8), 8) << 1;
            BranchTo(unchecked(_registers[ProgramCounter] + (uint)offset));
        }
    }

    void ExecuteThumbBranch(ushort instruction)
    {
        int offset = SignExtend(Bits(instruction, 0, 11), 11) << 1;
        BranchTo(unchecked(_registers[ProgramCounter] + (uint)offset));
    }

    void ExecuteThumbLongBranch(ushort instruction)
    {
        bool secondHalf = Bits(instruction, 11, 1) != 0;
        if (!secondHalf)
        {
            int offset = SignExtend(Bits(instruction, 0, 11), 11) << 12;
            WriteRegister(14, unchecked(_registers[ProgramCounter] + (uint)offset));
            return;
        }

        uint target = ReadRegister(14) + (Bits(instruction, 0, 11) << 1);
        uint returnAddress = (_registers[ProgramCounter] - 2u) | 1u;
        bool remainThumb = Bits(instruction, 12, 1) != 0;
        IsThumb = remainThumb;
        WriteRegister(14, returnAddress);
        BranchTo(remainThumb ? target & ~1u : target);
    }

    void ExecuteThumbUndefined(ushort instruction)
    {
        ObserveUndefined(instruction);
        EnterException(ModeUnd, 0x04, ArmBank.Und);
    }

    void WriteLogicalResult(int destination, uint result)
    {
        WriteRegister(destination, result);
        SetNegativeAndZero(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SetArithmeticFlags(uint result, bool carry, bool overflow)
    {
        SetNegativeAndZero(result);
        SetFlag(FlagC, carry);
        SetFlag(FlagV, overflow);
    }
}
