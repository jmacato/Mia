// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

using System.Runtime.CompilerServices;

namespace Arm7Core;

public sealed partial class Arm7Tdmi
{
    void ExecuteArmDataProcessing(uint instruction)
    {
        int opcode = (int)Bits(instruction, 21, 4);
        bool setFlags = Bits(instruction, 20, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        int rd = (int)Bits(instruction, 12, 4);
        Arm7TdmiShifterOperand shifter = ReadArmShifterOperand(instruction);
        uint operand1 = ReadOperand(rn, shifter.PipelinePcOffset);
        Arm7TdmiDataProcessingResult operation = ComputeArmDataProcessing(
            opcode,
            operand1,
            shifter.Value);
        bool writesResult = opcode is not (8 or 9 or 10 or 11);

        if (writesResult)
        {
            WriteRegister(rd, operation.Value);
        }
        if (!setFlags)
        {
            return;
        }

        SetArmDataProcessingFlags(operation, shifter.Carry);
        RestoreStatusAfterProgramCounterWrite(rd);
    }

    Arm7TdmiShifterOperand ReadArmShifterOperand(uint instruction)
    {
        if (Bits(instruction, 25, 1) != 0)
        {
            int rotation = (int)Bits(instruction, 8, 4) * 2;
            uint value = RotateRight(Bits(instruction, 0, 8), rotation);
            bool? carry = rotation == 0 ? null : (value & 0x80000000u) != 0;
            return new(value, carry, 0);
        }

        (uint amount, bool fromRegister, int pcOffset) =
            ReadArmShiftAmount(instruction);
        int rm = (int)Bits(instruction, 0, 4);
        Arm7TdmiShiftResult shifted = Shift(new(
            ReadOperand(rm, pcOffset),
            (int)Bits(instruction, 5, 2),
            amount,
            fromRegister));
        return new(shifted.Value, shifted.Carry, pcOffset);
    }

    (uint Amount, bool FromRegister, int PcOffset) ReadArmShiftAmount(
        uint instruction)
    {
        bool fromRegister = Bits(instruction, 4, 1) != 0;
        if (!fromRegister)
        {
            return (Bits(instruction, 7, 5), false, 0);
        }

        int register = (int)Bits(instruction, 8, 4);
        uint amount = ReadRegister(register) & 0xffu;
        Idle();
        return (amount, true, 4);
    }

    Arm7TdmiDataProcessingResult ComputeArmDataProcessing(
        int opcode,
        uint left,
        uint right) => opcode switch
        {
            0 or 8 => LogicalResult(left & right),
            1 or 9 => LogicalResult(left ^ right),
            2 or 10 => ArithmeticResult(AddWithCarry(left, ~right, true)),
            3 => ArithmeticResult(AddWithCarry(right, ~left, true)),
            4 or 11 => ArithmeticResult(AddWithCarry(left, right, false)),
            5 => ArithmeticResult(AddWithCarry(left, right, Flag(FlagC))),
            6 => ArithmeticResult(AddWithCarry(left, ~right, Flag(FlagC))),
            7 => ArithmeticResult(AddWithCarry(right, ~left, Flag(FlagC))),
            12 => LogicalResult(left | right),
            13 => LogicalResult(right),
            14 => LogicalResult(left & ~right),
            _ => LogicalResult(~right),
        };

    static Arm7TdmiDataProcessingResult LogicalResult(uint value) =>
        new(value, Arithmetic: false, Carry: false, Overflow: false);

    static Arm7TdmiDataProcessingResult ArithmeticResult(
        Arm7TdmiAluResult result) => new(
            result.Value,
            Arithmetic: true,
            result.Carry,
            result.Overflow);

    void SetArmDataProcessingFlags(
        Arm7TdmiDataProcessingResult operation,
        bool? shifterCarry)
    {
        SetNegativeAndZero(operation.Value);
        if (operation.Arithmetic)
        {
            SetFlag(FlagC, operation.Carry);
            SetFlag(FlagV, operation.Overflow);
            return;
        }
        if (shifterCarry.HasValue)
        {
            SetFlag(FlagC, shifterCarry.Value);
        }
    }

    void RestoreStatusAfterProgramCounterWrite(int destination)
    {
        // The Rd field still selects SPSR-to-CPSR restoration for the test
        // operations, even though TST/TEQ/CMP/CMN do not write Rd.
        if (destination == ProgramCounter)
        {
            _registers[Cpsr] = ReadCurrentSpsr();
            LatchInterruptDisable();
        }
    }

    void ExecuteArmMultiply(uint instruction)
    {
        bool accumulate = Bits(instruction, 21, 1) != 0;
        bool setFlags = Bits(instruction, 20, 1) != 0;
        int rd = (int)Bits(instruction, 16, 4);
        int rn = (int)Bits(instruction, 12, 4);
        uint multiplier = ReadOperand((int)Bits(instruction, 8, 4), 4);
        uint multiplicand = ReadOperand((int)Bits(instruction, 0, 4), 4);
        ulong accumulator = accumulate ? ReadOperand(rn, 4) : 0u;

        IdleMultiply(multiplier, signed: true, longMultiply: false);
        Arm7TdmiMultiplyResult multiplication = BoothMultiply(
            Arm7TdmiMultiplyFlavor.Short,
            multiplicand,
            multiplier,
            accumulator);
        uint result = (uint)multiplication.Value;
        if (accumulate)
        {
            Idle();
        }
        WriteRegister(rd, result);

        if (setFlags)
        {
            SetNegativeAndZero(result);
            SetFlag(FlagC, multiplication.Carry);
        }
    }

    void ExecuteArmMultiplyLong(uint instruction)
    {
        bool signed = Bits(instruction, 22, 1) != 0;
        bool accumulate = Bits(instruction, 21, 1) != 0;
        bool setFlags = Bits(instruction, 20, 1) != 0;
        int rdHigh = (int)Bits(instruction, 16, 4);
        int rdLow = (int)Bits(instruction, 12, 4);
        uint multiplier = ReadOperand((int)Bits(instruction, 8, 4), 4);
        uint multiplicand = ReadOperand((int)Bits(instruction, 0, 4), 4);

        ulong addend = accumulate
            ? ((ulong)ReadOperand(rdHigh, 4) << 32) | ReadOperand(rdLow, 4)
            : 0ul;
        Arm7TdmiMultiplyResult multiplication = BoothMultiply(
            signed ? Arm7TdmiMultiplyFlavor.LongSigned : Arm7TdmiMultiplyFlavor.LongUnsigned,
            multiplicand,
            multiplier,
            addend);
        ulong result = multiplication.Value;
        IdleMultiply(multiplier, signed, longMultiply: true);
        if (accumulate)
        {
            Idle();
        }

        WriteRegister(rdLow, (uint)result);
        WriteRegister(rdHigh, (uint)(result >> 32));
        if (setFlags)
        {
            SetFlag(FlagN, (result & 0x8000000000000000ul) != 0);
            SetFlag(FlagZ, result == 0);
            SetFlag(FlagC, multiplication.Carry);
        }
    }

    uint ReadOperand(int register, int pcExtra) =>
        ReadRegister(register) + (register == ProgramCounter ? (uint)pcExtra : 0u);

    Arm7TdmiShiftResult Shift(Arm7TdmiShiftOperand operand) =>
        operand.Type switch
        {
            0 => ShiftLeft(operand),
            1 => ShiftRightLogical(operand),
            2 => ShiftRightArithmetic(operand),
            _ => RotateRight(operand),
        };

    static Arm7TdmiShiftResult ShiftLeft(Arm7TdmiShiftOperand operand)
    {
        if (operand.Amount == 0)
        {
            return new(operand.Value, null);
        }
        if (operand.Amount < 32)
        {
            bool carry =
                ((operand.Value >> (int)(32 - operand.Amount)) & 1u) != 0;
            return new(operand.Value << (int)operand.Amount, carry);
        }

        bool finalCarry = operand.Amount == 32 && (operand.Value & 1u) != 0;
        return new(0, finalCarry);
    }

    static Arm7TdmiShiftResult ShiftRightLogical(Arm7TdmiShiftOperand operand)
    {
        if (operand.Amount == 0 && operand.RegisterSpecified)
        {
            return new(operand.Value, null);
        }

        uint amount = operand.Amount == 0 ? 32 : operand.Amount;
        if (amount < 32)
        {
            bool carry = ((operand.Value >> (int)(amount - 1)) & 1u) != 0;
            return new(operand.Value >> (int)amount, carry);
        }

        bool finalCarry = amount == 32 && (operand.Value & 0x80000000u) != 0;
        return new(0, finalCarry);
    }

    static Arm7TdmiShiftResult ShiftRightArithmetic(
        Arm7TdmiShiftOperand operand)
    {
        if (operand.Amount == 0 && operand.RegisterSpecified)
        {
            return new(operand.Value, null);
        }

        uint amount = operand.Amount == 0 ? 32 : operand.Amount;
        if (amount < 32)
        {
            bool carry = ((operand.Value >> (int)(amount - 1)) & 1u) != 0;
            return new((uint)((int)operand.Value >> (int)amount), carry);
        }

        bool finalCarry = (operand.Value & 0x80000000u) != 0;
        return new(finalCarry ? uint.MaxValue : 0, finalCarry);
    }

    Arm7TdmiShiftResult RotateRight(Arm7TdmiShiftOperand operand)
    {
        if (operand.Amount == 0 && operand.RegisterSpecified)
        {
            return new(operand.Value, null);
        }
        if (operand.Amount == 0)
        {
            bool carry = (operand.Value & 1u) != 0;
            uint value = (operand.Value >> 1) |
                (Flag(FlagC) ? 0x80000000u : 0u);
            return new(value, carry);
        }

        uint result = RotateRight(operand.Value, (int)(operand.Amount & 31u));
        return new(result, (result & 0x80000000u) != 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Arm7TdmiAluResult AddWithCarry(
        uint left,
        uint right,
        bool carryIn)
    {
        ulong unsignedResult = (ulong)left + right + (carryIn ? 1ul : 0ul);
        long signedResult = (long)(int)left + (int)right + (carryIn ? 1L : 0L);
        return new(
            (uint)unsignedResult,
            unsignedResult > uint.MaxValue,
            signedResult > int.MaxValue || signedResult < int.MinValue);
    }

    void IdleMultiply(uint multiplier, bool signed, bool longMultiply)
    {
        int cycles = CountMultiplyCycles(multiplier, signed) +
            (longMultiply ? 1 : 0);
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            Idle();
        }
    }

    static int CountMultiplyCycles(uint multiplier, bool signed)
    {
        if ((multiplier & 0xffffff00u) == 0 ||
            (signed && (multiplier & 0xffffff00u) == 0xffffff00u))
        {
            return 1;
        }
        if ((multiplier & 0xffff0000u) == 0 ||
            (signed && (multiplier & 0xffff0000u) == 0xffff0000u))
        {
            return 2;
        }
        if ((multiplier & 0xff000000u) == 0 ||
            (signed && (multiplier & 0xff000000u) == 0xff000000u))
        {
            return 3;
        }
        return 4;
    }
}
