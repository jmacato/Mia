// SPDX-License-Identifier: MIT

using Xunit;

namespace AvrCore.Tests;

public sealed class InstructionFastPathTests
{
    [Fact]
    public void IndexedDispatchMatchesReferenceForEveryOpcode()
    {
        Cpu indexed = new(new byte[0x20002], 4096);
        Cpu reference = new(new byte[0x20002], 4096);
        for (var opcode = 0; opcode <= ushort.MaxValue; opcode++)
        {
            Initialize(indexed, opcode);
            indexed.SetProgWord(2, 0x0200);
            indexed.Data[0x5b] = 0;
            indexed.Data[0x5c] = 0;
            indexed.Data.CopyTo(reference.Data, 0);
            indexed.ProgBytes.CopyTo(reference.ProgBytes, 0);
            reference.PC = indexed.PC;
            reference.Cycles = indexed.Cycles;

            AvrInstruction.Execute(indexed, opcode);
            AvrInstruction.ExecuteSlow(reference, opcode);

            Assert.True(indexed.Data.AsSpan().SequenceEqual(reference.Data),
                $"data mismatch for opcode 0x{opcode:x4}");
            Assert.Equal(reference.PC, indexed.PC);
            Assert.Equal(reference.Cycles, indexed.Cycles);
        }
    }

    [Fact]
    public void EveryFastOpcodeMatchesReferenceDispatcher()
    {
        Cpu fast = new(new byte[32], 4096);
        Cpu reference = new(new byte[32], 4096);
        var fastOpcodeCount = 0;

        for (var opcode = 0; opcode <= ushort.MaxValue; opcode++)
        {
            Initialize(fast, opcode);
            fast.Data.CopyTo(reference.Data, 0);
            fast.ProgBytes.CopyTo(reference.ProgBytes, 0);
            reference.PC = fast.PC;
            reference.Cycles = fast.Cycles;

            if (!AvrInstruction.TryExecuteFast(fast, opcode))
            {
                continue;
            }

            fastOpcodeCount++;
            AvrInstruction.Advance(fast);
            AvrInstruction.ExecuteSlow(reference, opcode);

            Assert.True(
                fast.Data.AsSpan().SequenceEqual(reference.Data),
                $"data mismatch for opcode 0x{opcode:x4}");
            Assert.Equal(reference.PC, fast.PC);
            Assert.Equal(reference.Cycles, fast.Cycles);
        }

        Assert.True(fastOpcodeCount > 40_000);
    }

    [Theory]
    [InlineData(0xd000)]
    [InlineData(0xd7ff)]
    [InlineData(0xd800)]
    [InlineData(0xdfff)]
    [InlineData(0x9508)]
    public void FastCallAndReturnMatchReferenceWith22BitPc(int opcode)
    {
        Cpu fast = new(new byte[0x20002], 4096);
        Cpu reference = new(new byte[0x20002], 4096);
        Initialize(fast, opcode);
        fast.Data.CopyTo(reference.Data, 0);
        fast.ProgBytes.CopyTo(reference.ProgBytes, 0);
        reference.PC = fast.PC;
        reference.Cycles = fast.Cycles;

        Assert.True(AvrInstruction.TryExecuteFast(fast, opcode));
        AvrInstruction.Advance(fast);
        AvrInstruction.ExecuteSlow(reference, opcode);

        Assert.True(fast.Data.AsSpan().SequenceEqual(reference.Data));
        Assert.Equal(reference.PC, fast.PC);
        Assert.Equal(reference.Cycles, fast.Cycles);
    }

    static void Initialize(Cpu cpu, int opcode)
    {
        for (var index = 0; index < cpu.Data.Length; index++)
        {
            cpu.Data[index] = (byte)(index * 73 + opcode * 29 + (opcode >> 8));
        }
        Array.Clear(cpu.ProgBytes);
        cpu.SetProgWord(2, (opcode & 1) == 0 ? 0x940c : 0x0000);
        cpu.SetUint16(26, 0x0200);
        cpu.SetUint16(28, 0x0300);
        cpu.SetUint16(30, 0x0400);
        cpu.SP = 0x0800;
        cpu.PC = 1;
        cpu.Cycles = 17;
    }
}
