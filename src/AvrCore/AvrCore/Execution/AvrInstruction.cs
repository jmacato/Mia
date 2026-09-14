// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

using System.Runtime.CompilerServices;

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsTwoWordInstruction(int opcode)
    {
        return (
            /* LDS */
            (opcode & 0xfe0f) == 0x9000 ||
            /* STS */
            (opcode & 0xfe0f) == 0x9200 ||
            /* CALL */
            (opcode & 0xfe0e) == 0x940e ||
            /* JMP */
            (opcode & 0xfe0e) == 0x940c
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Execute(Cpu cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        var opcode = cpu.GetProgWord(cpu.PC);
        Execute(cpu, opcode);
    }

    /// <summary>
    /// Executes an opcode already fetched from <paramref name="cpu"/>'s
    /// current program counter. This lets machine-level dispatchers inspect an
    /// instruction without paying for a second program-memory lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Execute(Cpu cpu, int opcode)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (TryExecuteFast(cpu, opcode))
        {
            Advance(cpu);
            return;
        }

        int instructionIndex = InstructionIndices[opcode];
        if (instructionIndex != byte.MaxValue)
        {
            SlowInstructions[instructionIndex].Handler(cpu, opcode);
        }
        Advance(cpu);
    }

    internal static void ExecuteSlow(Cpu cpu, int opcode)
    {
        foreach (AvrInstructionDefinition instruction in SlowInstructions)
        {
            if (!instruction.Matches(opcode))
            {
                continue;
            }

            instruction.Handler(cpu, opcode);
            break;
        }

        Advance(cpu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Advance(Cpu cpu)
    {
        var nextPc = cpu.PC + 1;
        cpu.PC = (uint)nextPc < (uint)cpu.ProgWords
            ? nextPc
            : WrapProgramCounter(nextPc, cpu.ProgWords);
        cpu.Cycles++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int WrapProgramCounter(int programCounter, int programWords)
    {
        int wrapped = programCounter % programWords;
        return wrapped < 0 ? wrapped + programWords : wrapped;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryExecuteFast(Cpu cpu, int opcode) =>
        ((uint)opcode >> 10) switch
        {
            0 => opcode == 0, // NOP
            1 => ExecuteFastCpc(cpu, opcode),
            2 => ExecuteFastSbc(cpu, opcode),
            3 => ExecuteFastAdd(cpu, opcode),
            4 => ExecuteFastCpse(cpu, opcode),
            5 => ExecuteFastCp(cpu, opcode),
            6 => ExecuteFastSub(cpu, opcode),
            7 => ExecuteFastAdc(cpu, opcode),
            8 => ExecuteFastAnd(cpu, opcode),
            9 => ExecuteFastEor(cpu, opcode),
            10 => ExecuteFastOr(cpu, opcode),
            11 => ExecuteFastMov(cpu, opcode),
            >= 12 and <= 15 => ExecuteFastCpi(cpu, opcode),
            >= 16 and <= 19 => ExecuteFastSbci(cpu, opcode),
            >= 20 and <= 23 => ExecuteFastSubi(cpu, opcode),
            32 or 33 or 34 or 35 or 40 or 41 or 42 or 43 =>
                ExecuteFastIndirectTransfer(cpu, opcode),
            36 => ExecuteFastPostIncrementStore(cpu, opcode),
            37 => ExecuteFastReturnOrSbiw(cpu, opcode),
            44 or 45 => ExecuteFastIn(cpu, opcode),
            46 or 47 => ExecuteFastOut(cpu, opcode),
            >= 48 and <= 51 => ExecuteFastRelativeJump(cpu, opcode),
            >= 52 and <= 55 => ExecuteFastRelativeCall(cpu, opcode),
            >= 56 and <= 59 => ExecuteFastLoadImmediate(cpu, opcode),
            60 => ExecuteFastBranchIfSet(cpu, opcode),
            61 => ExecuteFastBranchIfClear(cpu, opcode),
            _ => false,
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastCpc(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int left = data[(opcode & 0x1f0) >> 4];
        int right = data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int sreg = data[95];
        int result = left - right - (sreg & 1);
        sreg = (sreg & 0xc0) |
            (result == 0 && ((sreg >> 1) & 1) != 0 ? 2 : 0) |
            (right + (sreg & 1) > left ? 1 : 0);
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastSbc(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int destination = (opcode & 0x1f0) >> 4;
        int left = data[destination];
        int right = data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int sreg = data[95];
        int result = left - right - (sreg & 1);
        data[destination] = (byte)result;
        sreg = (sreg & 0xc0) |
            (result == 0 && ((sreg >> 1) & 1) != 0 ? 2 : 0) |
            (right + (sreg & 1) > left ? 1 : 0);
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastAdd(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int destination = (opcode & 0x1f0) >> 4;
        int left = data[destination];
        int right = data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int result = (left + right) & 255;
        data[destination] = (byte)result;
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((result ^ right) & (result ^ left) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= ((left + right) & 256) != 0 ? 1 : 0;
        sreg |= (1 & ((left & right) | (right & ~result) | (~result & left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastCpse(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        if (data[(opcode & 0x1f0) >> 4] ==
            data[(opcode & 0xf) | ((opcode & 0x200) >> 5)])
        {
            int nextOpcode = cpu.GetProgWord(cpu.PC + 1);
            int skipSize = IsTwoWordInstruction(nextOpcode) ? 2 : 1;
            cpu.PC += skipSize;
            cpu.Cycles += skipSize;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastCp(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int left = data[(opcode & 0x1f0) >> 4];
        int right = data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int result = left - right;
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= right > left ? 1 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastSub(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int destination = (opcode & 0x1f0) >> 4;
        int left = data[destination];
        int right = data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int result = left - right;
        data[destination] = (byte)result;
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= right > left ? 1 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastAdc(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int destination = (opcode & 0x1f0) >> 4;
        int left = data[destination];
        int right = data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        int sum = left + right + (data[95] & 1);
        int result = sum & 255;
        data[destination] = (byte)result;
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((result ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (sum & 256) != 0 ? 1 : 0;
        sreg |= (1 & ((left & right) | (right & ~result) | (~result & left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastAnd(Cpu cpu, int opcode) =>
        ExecuteFastLogical(cpu, opcode, cpu.Data[(opcode & 0x1f0) >> 4] &
            cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastEor(Cpu cpu, int opcode) =>
        ExecuteFastLogical(cpu, opcode, cpu.Data[(opcode & 0x1f0) >> 4] ^
            cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastOr(Cpu cpu, int opcode) =>
        ExecuteFastLogical(cpu, opcode, cpu.Data[(opcode & 0x1f0) >> 4] |
            cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastLogical(Cpu cpu, int opcode, int result)
    {
        byte[] data = cpu.Data;
        data[(opcode & 0x1f0) >> 4] = (byte)result;
        int sreg = data[95] & 0xe1;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastMov(Cpu cpu, int opcode)
    {
        cpu.Data[(opcode & 0x1f0) >> 4] =
            cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastCpi(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int left = data[((opcode & 0xf0) >> 4) + 16];
        int right = (opcode & 0xf) | ((opcode & 0xf00) >> 4);
        int result = left - right;
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= right > left ? 1 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastSbci(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int destination = ((opcode & 0xf0) >> 4) + 16;
        int left = data[destination];
        int right = (opcode & 0xf) | ((opcode & 0xf00) >> 4);
        int sreg = data[95];
        int result = left - right - (sreg & 1);
        data[destination] = (byte)result;
        sreg = (sreg & 0xc0) |
            (result == 0 && ((sreg >> 1) & 1) != 0 ? 2 : 0) |
            (right + (sreg & 1) > left ? 1 : 0);
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastSubi(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int destination = ((opcode & 0xf0) >> 4) + 16;
        int left = data[destination];
        int right = (opcode & 0xf) | ((opcode & 0xf00) >> 4);
        int result = left - right;
        data[destination] = (byte)result;
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (128 & result) != 0 ? 4 : 0;
        sreg |= ((left ^ right) & (left ^ result) & 128) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= right > left ? 1 : 0;
        sreg |= (1 & ((~left & right) | (right & result) | (result & ~left))) != 0
            ? 0x20
            : 0;
        data[95] = (byte)sreg;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastIndirectTransfer(Cpu cpu, int opcode)
    {
        int displacement =
            (opcode & 7) | ((opcode & 0xc00) >> 7) | ((opcode & 0x2000) >> 8);
        int register = (opcode & 0x1f0) >> 4;
        return (opcode & 0xd208) switch
        {
            0x8000 => ExecuteFastIndirectLoad(
                cpu,
                register,
                cpu.GetDataAddress(30, 0x5b, displacement)),
            0x8008 => ExecuteFastIndirectLoad(
                cpu,
                register,
                cpu.GetDataAddress(28, 0x5a, displacement)),
            0x8200 => ExecuteFastIndirectStore(
                cpu,
                register,
                cpu.GetDataAddress(30, 0x5b, displacement)),
            0x8208 => ExecuteFastIndirectStore(
                cpu,
                register,
                cpu.GetDataAddress(28, 0x5a, displacement)),
            _ => false,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastIndirectLoad(
        Cpu cpu,
        int register,
        int address)
    {
        cpu.Cycles++;
        cpu.Data[register] = cpu.ReadData(address);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastIndirectStore(
        Cpu cpu,
        int register,
        int address)
    {
        cpu.WriteData(address, cpu.Data[register]);
        cpu.Cycles++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastPostIncrementStore(Cpu cpu, int opcode)
    {
        if ((opcode & 0xfe0f) != 0x9201)
        {
            return false;
        }

        int register = (opcode & 0x1f0) >> 4;
        int address = cpu.GetDataAddress(30, 0x5b);
        cpu.WriteData(address, cpu.Data[register]);
        cpu.SetDataAddress(30, 0x5b, address + 1);
        cpu.Cycles++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastReturnOrSbiw(Cpu cpu, int opcode)
    {
        if (opcode == 0x9508)
        {
            ExecuteFastReturn(cpu);
            return true;
        }
        if ((opcode & 0xff00) != 0x9700)
        {
            return false;
        }

        ExecuteFastSbiw(cpu, opcode);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ExecuteFastReturn(Cpu cpu)
    {
        bool pc22Bits = cpu.Pc22Bits;
        int stackPointer = cpu.GetUint16(93) + (pc22Bits ? 3 : 2);
        cpu.SetUint16(93, stackPointer);
        int returnAddress =
            (cpu.ReadData(stackPointer - 1) << 8) + cpu.ReadData(stackPointer);
        if (pc22Bits)
        {
            returnAddress |= cpu.ReadData(stackPointer - 2) << 16;
        }
        cpu.PC = returnAddress - 1;
        cpu.Cycles += pc22Bits ? 4 : 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ExecuteFastSbiw(Cpu cpu, int opcode)
    {
        byte[] data = cpu.Data;
        int register = 2 * ((opcode & 0x30) >> 4) + 24;
        int value = cpu.GetUint16(register);
        int immediate = (opcode & 0xf) | ((opcode & 0xc0) >> 2);
        int result = value - immediate;
        cpu.SetUint16(register, result);
        int sreg = data[95] & 0xc0;
        sreg |= result != 0 ? 0 : 2;
        sreg |= (0x8000 & result) != 0 ? 4 : 0;
        sreg |= (value & ~result & 0x8000) != 0 ? 8 : 0;
        sreg |= (((sreg >> 2) & 1) ^ ((sreg >> 3) & 1)) != 0 ? 0x10 : 0;
        sreg |= immediate > value ? 1 : 0;
        sreg |= (1 & ((~value & immediate) | (immediate & result) |
            (result & ~value))) != 0 ? 0x20 : 0;
        data[95] = (byte)sreg;
        cpu.Cycles++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastIn(Cpu cpu, int opcode)
    {
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(
            ((opcode & 0xf) | ((opcode & 0x600) >> 5)) + 32);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastOut(Cpu cpu, int opcode)
    {
        cpu.WriteData(
            ((opcode & 0xf) | ((opcode & 0x600) >> 5)) + 32,
            cpu.Data[(opcode & 0x1f0) >> 4]);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastRelativeJump(Cpu cpu, int opcode)
    {
        cpu.PC += (opcode & 0x7ff) - ((opcode & 0x800) != 0 ? 0x800 : 0);
        cpu.Cycles++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastRelativeCall(Cpu cpu, int opcode)
    {
        int offset = (opcode & 0x7ff) - ((opcode & 0x800) != 0 ? 0x800 : 0);
        int returnAddress = cpu.PC + 1;
        int stackPointer = cpu.GetUint16(93);
        bool pc22Bits = cpu.Pc22Bits;
        cpu.WriteData(stackPointer, (byte)returnAddress);
        cpu.WriteData(stackPointer - 1, (byte)(returnAddress >> 8));
        if (pc22Bits)
        {
            cpu.WriteData(stackPointer - 2, (byte)(returnAddress >> 16));
        }
        cpu.SetUint16(93, stackPointer - (pc22Bits ? 3 : 2));
        cpu.PC += offset;
        cpu.Cycles += pc22Bits ? 3 : 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastLoadImmediate(Cpu cpu, int opcode)
    {
        cpu.Data[((opcode & 0xf0) >> 4) + 16] =
            (byte)((opcode & 0xf) | ((opcode & 0xf00) >> 4));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastBranchIfSet(Cpu cpu, int opcode)
    {
        if ((cpu.Data[95] & (1 << (opcode & 7))) != 0)
        {
            ExecuteFastRelativeBranch(cpu, opcode);
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ExecuteFastBranchIfClear(Cpu cpu, int opcode)
    {
        if ((cpu.Data[95] & (1 << (opcode & 7))) == 0)
        {
            ExecuteFastRelativeBranch(cpu, opcode);
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ExecuteFastRelativeBranch(Cpu cpu, int opcode)
    {
        cpu.PC += ((opcode & 0x1f8) >> 3) -
            ((opcode & 0x200) != 0 ? 0x40 : 0);
        cpu.Cycles++;
    }
}
