// SPDX-License-Identifier: Zlib
//
// C# port of the ARM7TDMI Booth multiplier reconstruction from
// https://github.com/zaydlang/multiplication-algorithm.
// Copyright (c) 2024 zaydlang. Contributions by calc84maniac.
// This is an altered source version; see LICENSE.Multiplier.txt.

namespace Arm7Core;

public sealed partial class Arm7Tdmi
{
    const ulong BoothMask34 = 0x3fffffffful;
    const ulong BoothMask33 = 0x1fffffffful;

    static Arm7TdmiMultiplyResult BoothMultiply(
        Arm7TdmiMultiplyFlavor flavor,
        uint multiplicand32,
        uint multiplier32,
        ulong accumulator)
    {
        Arm7TdmiBoothState state = InitializeBoothState(
            flavor,
            multiplicand32,
            multiplier32,
            accumulator);
        RunBoothCycles(state);
        AlignBoothPartialResults(state);
        return state.LongResult
            ? CreateLongBoothResult(state)
            : CreateShortBoothResult(state);
    }

    static Arm7TdmiBoothState InitializeBoothState(
        Arm7TdmiMultiplyFlavor flavor,
        uint multiplicand32,
        uint multiplier32,
        ulong accumulator)
    {
        bool signed = flavor is Arm7TdmiMultiplyFlavor.Short or Arm7TdmiMultiplyFlavor.LongSigned;
        ulong multiplicand = signed
            ? SignExtendBits(multiplicand32, 32, 34)
            : multiplicand32 & BoothMask33;
        ulong multiplier = signed
            ? SignExtendBits(multiplier32, 32, 34)
            : multiplier32 & BoothMask33;
        bool adderCarryIn = (multiplier & 1ul) != 0;

        Arm7TdmiCsaResult csa = new(
            accumulator,
            adderCarryIn ? ~multiplicand : 0ul);
        ulong accumulatorShiftRegister = accumulator >> 34;

        UInt128 partialSum = csa.Output & 1ul;
        UInt128 partialCarry = csa.Carry & 1ul;
        csa = new(csa.Output >> 1, csa.Carry >> 1);
        partialSum = RotateRight128(partialSum, 1);
        partialCarry = RotateRight128(partialCarry, 1);

        return new()
        {
            Signed = signed,
            LongResult = flavor is not Arm7TdmiMultiplyFlavor.Short,
            Multiplicand = multiplicand,
            Multiplier = multiplier,
            AdderCarryIn = adderCarryIn,
            Csa = csa,
            AccumulatorShiftRegister = accumulatorShiftRegister,
            PartialSum = partialSum,
            PartialCarry = partialCarry,
        };
    }

    static void RunBoothCycles(Arm7TdmiBoothState state)
    {
        do
        {
            ulong accumulatorShiftRegister = state.AccumulatorShiftRegister;
            state.Csa = BoothCycle(
                state.Csa,
                state.Multiplicand,
                state.Multiplier,
                ref accumulatorShiftRegister);
            state.AccumulatorShiftRegister = accumulatorShiftRegister;
            state.PartialSum |= state.Csa.Output & 0xfful;
            state.PartialCarry |= state.Csa.Carry & 0xfful;
            state.Csa = new(state.Csa.Output >> 8, state.Csa.Carry >> 8);
            state.PartialSum = RotateRight128(state.PartialSum, 8);
            state.PartialCarry = RotateRight128(state.PartialCarry, 8);
            state.Multiplier = ArithmeticShiftRight33(state.Multiplier, 8);
            state.Iterations++;
        }
        while (!ShouldTerminate(state.Multiplier, state.Signed));
    }

    static void AlignBoothPartialResults(Arm7TdmiBoothState state)
    {
        state.PartialSum |= state.Csa.Output;
        state.PartialCarry |= state.Csa.Carry;
        int correction = state.Iterations switch
        {
            1 => 23,
            2 => 15,
            3 => 7,
            _ => 31,
        };
        state.PartialSum = RotateRight128(state.PartialSum, correction);
        state.PartialCarry = RotateRight128(state.PartialCarry, correction);
    }

    static Arm7TdmiMultiplyResult CreateLongBoothResult(
        Arm7TdmiBoothState state)
    {
        ulong sumHigh = (ulong)(state.PartialSum >> 64);
        ulong carryHigh = (ulong)(state.PartialCarry >> 64);
        if (state.Iterations == 4)
        {
            Arm7TdmiAdderResult low = Add32(
                (uint)sumHigh,
                (uint)carryHigh,
                state.AdderCarryIn);
            Arm7TdmiAdderResult high = Add32(
                (uint)(sumHigh >> 32),
                (uint)(carryHigh >> 32),
                low.Carry);
            return new(((ulong)high.Value << 32) | low.Value, (carryHigh >> 63) != 0);
        }

        Arm7TdmiAdderResult lowPartial = Add32(
            (uint)(sumHigh >> 32),
            (uint)(carryHigh >> 32),
            state.AdderCarryIn);
        int shift = 2 + 8 * state.Iterations;
        ulong carryLow = SignExtendBits((ulong)state.PartialCarry, shift, 64);
        ulong sumLow = (ulong)state.PartialSum |
            (state.AccumulatorShiftRegister << shift);
        Arm7TdmiAdderResult highPartial = Add32(
            (uint)sumLow,
            (uint)carryLow,
            lowPartial.Carry);
        return new(
            ((ulong)highPartial.Value << 32) | lowPartial.Value,
            (carryHigh >> 63) != 0);
    }

    static Arm7TdmiMultiplyResult CreateShortBoothResult(
        Arm7TdmiBoothState state)
    {
        ulong sumHigh = (ulong)(state.PartialSum >> 64);
        ulong carryHigh = (ulong)(state.PartialCarry >> 64);
        if (state.Iterations == 4)
        {
            Arm7TdmiAdderResult output = Add32(
                (uint)sumHigh,
                (uint)carryHigh,
                state.AdderCarryIn);
            return new(output.Value, ((carryHigh >> 31) & 1ul) != 0);
        }

        Arm7TdmiAdderResult shortOutput = Add32(
            (uint)(sumHigh >> 32),
            (uint)(carryHigh >> 32),
            state.AdderCarryIn);
        return new(shortOutput.Value, (carryHigh >> 63) != 0);
    }

    static Arm7TdmiCsaResult BoothCycle(
        Arm7TdmiCsaResult previous,
        ulong multiplicand,
        ulong multiplier,
        ref ulong accumulatorShiftRegister)
    {
        Arm7TdmiCsaResult current = previous;
        Arm7TdmiCsaResult final = default;
        for (int index = 0; index < 4; index++)
        {
            ulong previousCarry = current.Carry & BoothMask33;
            current = new(current.Output & BoothMask33, previousCarry);
            Arm7TdmiBoothTerm term = BoothRecode(multiplicand, (int)((multiplier >> (2 * index)) & 7ul));
            Arm7TdmiCsaResult result = CarrySaveAdd(current.Output, term.Value & BoothMask33, current.Carry);
            result = new(result.Output, (result.Carry << 1) | (term.Carry ? 1ul : 0ul));

            final = new(
                final.Output | ((result.Output & 3ul) << (2 * index)),
                final.Carry | ((result.Carry & 3ul) << (2 * index)));
            result = new(result.Output >> 2, result.Carry >> 2);

            ulong magic = Bit(accumulatorShiftRegister, 0) +
                (Bit(previousCarry, 32) == 0 ? 1ul : 0ul) +
                (Bit(term.Value, 33) == 0 ? 1ul : 0ul);
            result = new(
                result.Output | (magic << 31),
                result.Carry | ((Bit(accumulatorShiftRegister, 1) == 0 ? 1ul : 0ul) << 32));
            accumulatorShiftRegister >>= 2;
            current = result;
        }

        return new(
            final.Output | (current.Output << 8),
            final.Carry | (current.Carry << 8));
    }

    static Arm7TdmiBoothTerm BoothRecode(ulong input, int chunk)
    {
        (ulong value, bool carry) = chunk switch
        {
            0 => (0ul, false),
            1 or 2 => (input, false),
            3 => (2ul * input, false),
            4 => (~(2ul * input), true),
            5 or 6 => (~input, true),
            _ => (0ul, false),
        };
        return new(value & BoothMask34, carry);
    }

    static Arm7TdmiCsaResult CarrySaveAdd(ulong left, ulong middle, ulong right) => new(
        left ^ middle ^ right,
        (left & middle) | (middle & right) | (right & left));

    static Arm7TdmiAdderResult Add32(uint left, uint right, bool carry)
    {
        ulong full = (ulong)left + right + (carry ? 1ul : 0ul);
        return new((uint)full, full > uint.MaxValue);
    }

    static bool ShouldTerminate(ulong multiplier, bool signed) =>
        multiplier == 0 || (signed && multiplier == BoothMask33);

    static ulong ArithmeticShiftRight33(ulong value, int amount)
    {
        value &= BoothMask33;
        if ((value & (1ul << 32)) != 0)
        {
            value |= ~BoothMask33;
        }
        return unchecked((ulong)((long)value >> amount)) & BoothMask33;
    }

    static ulong SignExtendBits(ulong value, int sourceBits, int destinationBits)
    {
        ulong sourceMask = sourceBits == 64 ? ulong.MaxValue : (1ul << sourceBits) - 1ul;
        ulong destinationMask = destinationBits == 64
            ? ulong.MaxValue
            : (1ul << destinationBits) - 1ul;
        value &= sourceMask;
        if ((value & (1ul << (sourceBits - 1))) != 0)
        {
            value |= destinationMask & ~sourceMask;
        }
        return value & destinationMask;
    }

    static ulong Bit(ulong value, int bit) => (value >> bit) & 1ul;

    static UInt128 RotateRight128(UInt128 value, int amount) =>
        (value >> amount) | (value << (128 - amount));
}
