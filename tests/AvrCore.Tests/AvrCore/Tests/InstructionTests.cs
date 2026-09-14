// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.spec.ts)

using Xunit;

namespace AvrCore.Tests;

public class InstructionTests
{
    const int R0 = 0;
    const int R1 = 1;
    const int R2 = 2;
    const int R3 = 3;
    const int R4 = 4;
    const int R5 = 5;
    const int R6 = 6;
    const int R7 = 7;
    const int R8 = 8;
    const int R16 = 16;
    const int R17 = 17;
    const int R18 = 18;
    const int R19 = 19;
    const int R20 = 20;
    const int R21 = 21;
    const int R22 = 22;
    const int R23 = 23;
    const int R24 = 24;
    const int R26 = 26;
    const int R27 = 27;
    const int R31 = 31;
    const int X = 26;
    const int Y = 28;
    const int Z = 30;
    const int RAMPD = 0x58;
    const int RAMPX = 0x59;
    const int RAMPY = 0x5a;
    const int RAMPZ = 0x5b;
    const int EIND = 0x5c;
    const int SP = 93;
    const int SPH = 94;
    const int SREG = 95;

    // SREG Bits: I-HSVNZC
    const byte SREG_C = 0b00000001;
    const byte SREG_Z = 0b00000010;
    const byte SREG_N = 0b00000100;
    const byte SREG_V = 0b00001000;
    const byte SREG_S = 0b00010000;
    const byte SREG_H = 0b00100000;
    const byte SREG_I = 0b10000000;

    Cpu _cpu = new(new byte[0x10000]);

    void LoadProgram(params string[] instructions)
    {
        var result = Assembler.Assemble(string.Join("\n", instructions));
        Assert.Empty(result.Errors);
        result.Bytes.CopyTo(_cpu.ProgBytes, 0);
    }

    [Fact]
    public void ExecuteWithFetchedOpcodeDoesNotReadCurrentProgramWordAgain()
    {
        _cpu.ProgramWordReadHook = _ =>
            throw new InvalidOperationException("unexpected program-memory read");

        AvrInstruction.Execute(_cpu, 0xe02a); // LDI r18, 0x0a

        Assert.Equal(0x0a, _cpu.Data[R18]);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
    }

    [Fact]
    public void AdcWhenCarryIsOn()
    {
        LoadProgram("ADC r0, r1");
        _cpu.Data[R0] = 10;
        _cpu.Data[R1] = 20;
        _cpu.Data[SREG] = SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(31, _cpu.Data[R0]);
        Assert.Equal(0, _cpu.Data[SREG]);
    }

    [Fact]
    public void AdcWhenCarryIsOnAndResultOverflows()
    {
        LoadProgram("ADC r0, r1");
        _cpu.Data[R0] = 10;
        _cpu.Data[R1] = 245;
        _cpu.Data[SREG] = SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0, _cpu.Data[R0]);
        Assert.Equal(SREG_H | SREG_Z | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void AddWhenResultOverflows()
    {
        LoadProgram("ADD r0, r1");
        _cpu.Data[R0] = 11;
        _cpu.Data[R1] = 245;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0, _cpu.Data[R0]);
        Assert.Equal(SREG_H | SREG_Z | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void AddWhenCarryIsOn()
    {
        LoadProgram("ADD r0, r1");
        _cpu.Data[R0] = 11;
        _cpu.Data[R1] = 244;
        _cpu.Data[SREG] = SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(255, _cpu.Data[R0]);
        Assert.Equal(SREG_S | SREG_N, _cpu.Data[SREG]);
    }

    [Fact]
    public void AddWhenCarryIsOnAndResultOverflows()
    {
        LoadProgram("ADD r0, r1");
        _cpu.Data[R0] = 11;
        _cpu.Data[R1] = 245;
        _cpu.Data[SREG] = SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0, _cpu.Data[R0]);
        Assert.Equal(SREG_H | SREG_Z | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Bclr()
    {
        LoadProgram("BCLR 2");
        _cpu.Data[SREG] = 0xff;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0xfb, _cpu.Data[SREG]);
    }

    [Fact]
    public void Bld()
    {
        LoadProgram("BLD r4, 7");
        _cpu.Data[R4] = 0x15;
        _cpu.Data[SREG] = 0x40;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x95, _cpu.Data[R4]);
        Assert.Equal(0x40, _cpu.Data[SREG]);
    }

    [Fact]
    public void BrbcWhenFlagIsClear()
    {
        LoadProgram("BRBC 0, +8");
        _cpu.Data[SREG] = SREG_V;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1 + 8 / 2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
    }

    [Fact]
    public void BrbcWhenFlagIsSet()
    {
        LoadProgram("BRBC 0, +8");
        _cpu.Data[SREG] = SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
    }

    [Fact]
    public void BrbsForwardWhenFlagIsSet()
    {
        LoadProgram("BRBS 3, 92");
        _cpu.Data[SREG] = SREG_V;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1 + 92 / 2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
    }

    [Fact]
    public void BrbsBackwardWhenFlagIsSet()
    {
        LoadProgram("BRBS 3, -4");
        _cpu.Data[SREG] = SREG_V;
        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles); // 1 for NOP, 2 for BRBS
    }

    [Fact]
    public void BrbsBackwardWhenFlagIsClear()
    {
        LoadProgram("BRBS 3, -4");
        _cpu.Data[SREG] = 0x0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
    }

    [Fact]
    public void Call()
    {
        LoadProgram("CALL 0xb8");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 150;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x5c, _cpu.PC);
        Assert.Equal(4, _cpu.Cycles);
        Assert.Equal(2, _cpu.Data[150]); // return addr
        Assert.Equal(148, _cpu.Data[SP]); // SP should be decremented
    }

    [Fact]
    public void CallPushesThreeByteReturnAddressOnLargeFlash()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("CALL 0xb8");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 150;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x5c, _cpu.PC);
        Assert.Equal(5, _cpu.Cycles);
        Assert.Equal(2, _cpu.Data[150]); // return addr
        Assert.Equal(147, _cpu.Data[SP]); // SP should be decremented by 3
    }

    [Fact]
    public void Cbi()
    {
        LoadProgram("CBI 0x0c, 5");
        _cpu.Data[0x2c] = 0b11111111;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0b11011111, _cpu.Data[0x2c]);
    }

    [Fact]
    public void Cpc()
    {
        LoadProgram("CPC r27, r18");
        _cpu.Data[R18] = 0x1;
        _cpu.Data[R27] = 0x1;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0, _cpu.Data[SREG]);
    }

    [Fact]
    public void CpcWithCarryIn()
    {
        LoadProgram("CPC r24, r1");
        _cpu.Data[R1] = 0;
        _cpu.Data[R24] = 0;
        _cpu.Data[SREG] = SREG_I | SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(SREG_I | SREG_H | SREG_S | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Cpi()
    {
        LoadProgram("CPI r26, 0x9");
        _cpu.Data[R26] = 0x8;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(SREG_H | SREG_S | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void CpseWhenNotEqual()
    {
        LoadProgram("CPSE r2, r3");
        _cpu.Data[R2] = 10;
        _cpu.Data[R3] = 11;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
    }

    [Fact]
    public void CpseWhenEqual()
    {
        LoadProgram("CPSE r2, r3");
        _cpu.Data[R2] = 10;
        _cpu.Data[R3] = 10;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
    }

    [Fact]
    public void CpseWhenEqualFollowedByTwoWordInstruction()
    {
        LoadProgram("CPSE r2, r3", "CALL 8");
        _cpu.Data[R2] = 10;
        _cpu.Data[R3] = 10;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(3, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
    }

    [Fact]
    public void Eicall()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("EICALL");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x80;
        _cpu.Data[EIND] = 1;
        _cpu.SetUint16(Z, 0x1234);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x11234, _cpu.PC);
        Assert.Equal(4, _cpu.Cycles);
        Assert.Equal(0x80 - 3, _cpu.Data[SP]); // according to datasheet: SP <- SP - 3
        Assert.Equal(1, _cpu.Data[0x80]); // Return address
    }

    [Fact]
    public void Eijmp()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("EIJMP");
        _cpu.Data[EIND] = 1;
        _cpu.SetUint16(Z, 0x1040);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x11040, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
    }

    [Fact]
    public void Elpm()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("ELPM");
        _cpu.Data[Z] = 0x50;
        _cpu.Data[RAMPZ] = 0x2;
        _cpu.ProgBytes[0x20050] = 0x62; // value to be loaded
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x62, _cpu.Data[R0]); // check that value was loaded to r0
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
    }

    [Fact]
    public void ElpmRegister()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("ELPM r5, Z");
        _cpu.Data[Z] = 0x11;
        _cpu.Data[RAMPZ] = 0x1;
        _cpu.ProgBytes[0x10011] = 0x99; // value to be loaded
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x99, _cpu.Data[R5]); // check that value was loaded to r5
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
    }

    [Fact]
    public void ElpmPostIncrement()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("ELPM r6, Z+");
        _cpu.SetUint16(Z, 0xffff);
        _cpu.Data[RAMPZ] = 0x2;
        _cpu.ProgBytes[0x2ffff] = 0x22; // value to be loaded
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
        Assert.Equal(0x22, _cpu.Data[R6]); // check that value was loaded to r6
        Assert.Equal(0x0, _cpu.GetUint16(Z)); // verify that Z was incremented
        Assert.Equal(3, _cpu.Data[RAMPZ]); // verify that RAMPZ was incremented
    }

    [Fact]
    public void ElpmPostIncrementClampsRampz()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("ELPM r6, Z+");
        _cpu.SetUint16(Z, 0xffff);
        _cpu.Data[RAMPZ] = 0x3;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x0, _cpu.Data[RAMPZ]); // verify that RAMPZ was reset to zero
    }

    [Fact]
    public void Icall()
    {
        LoadProgram("ICALL");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x80;
        _cpu.SetUint16(Z, 0x2020);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(3, _cpu.Cycles);
        Assert.Equal(0x2020, _cpu.PC);
        Assert.Equal(1, _cpu.Data[0x80]); // Return address
        Assert.Equal(0x7e, _cpu.Data[SP]); // SP should decrement by 2
    }

    [Fact]
    public void IcallPushesThreeByteReturnAddressOnLargeFlash()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("ICALL");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x80;
        _cpu.SetUint16(Z, 0x2020);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(4, _cpu.Cycles);
        Assert.Equal(0x2020, _cpu.PC);
        Assert.Equal(1, _cpu.Data[0x80]); // Return address
        Assert.Equal(0x7d, _cpu.Data[SP]); // SP should decrement by 3
    }

    [Fact]
    public void Ijmp()
    {
        LoadProgram("IJMP");
        _cpu.SetUint16(Z, 0x1040);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x1040, _cpu.PC);
    }

    [Fact]
    public void In()
    {
        LoadProgram("IN r5, 0xb");
        _cpu.Data[0x2b] = 0xaf;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(0xaf, _cpu.Data[R5]);
    }

    [Fact]
    public void Inc()
    {
        LoadProgram("INC r5");
        _cpu.Data[R5] = 0x7f;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x80, _cpu.Data[R5]);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(SREG_N | SREG_V, _cpu.Data[SREG]);
    }

    [Fact]
    public void IncWhenValueIs0xff()
    {
        LoadProgram("INC r5");
        _cpu.Data[R5] = 0xff;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0, _cpu.Data[R5]);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(SREG_Z, _cpu.Data[SREG]);
    }

    [Fact]
    public void Jmp()
    {
        LoadProgram("JMP 0xb8");
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x5c, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
    }

    [Fact]
    public void Lac()
    {
        LoadProgram("LAC Z, r19");
        _cpu.Data[R19] = 0x02;
        _cpu.SetUint16(Z, 0x100);
        _cpu.Data[0x100] = 0x96;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x96, _cpu.Data[R19]);
        Assert.Equal(0x100, _cpu.GetUint16(Z));
        Assert.Equal(0x94, _cpu.Data[0x100]);
    }

    [Fact]
    public void Las()
    {
        LoadProgram("LAS Z, r17");
        _cpu.Data[R17] = 0x11;
        _cpu.Data[Z] = 0x80;
        _cpu.Data[0x80] = 0x44;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x44, _cpu.Data[R17]);
        Assert.Equal(0x80, _cpu.Data[Z]);
        Assert.Equal(0x55, _cpu.Data[0x80]);
    }

    [Fact]
    public void Lat()
    {
        LoadProgram("LAT Z, r0");
        _cpu.Data[R0] = 0x33;
        _cpu.Data[Z] = 0x80;
        _cpu.Data[0x80] = 0x66;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x66, _cpu.Data[R0]);
        Assert.Equal(0x80, _cpu.Data[Z]);
        Assert.Equal(0x55, _cpu.Data[0x80]);
    }

    [Fact]
    public void LdX()
    {
        LoadProgram("LD r1, X");
        _cpu.Data[0xc0] = 0x15;
        _cpu.Data[X] = 0xc0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x15, _cpu.Data[R1]);
        Assert.Equal(0xc0, _cpu.Data[X]); // verify that X was unchanged
    }

    [Fact]
    public void LdXPostIncrement()
    {
        LoadProgram("LD r17, X+");
        _cpu.Data[0xc0] = 0x15;
        _cpu.Data[X] = 0xc0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x15, _cpu.Data[R17]);
        Assert.Equal(0xc1, _cpu.Data[X]); // verify that X was incremented
    }

    [Fact]
    public void LdXPreDecrement()
    {
        LoadProgram("LD r1, -X");
        _cpu.Data[0x98] = 0x22;
        _cpu.Data[X] = 0x99;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x22, _cpu.Data[R1]);
        Assert.Equal(0x98, _cpu.Data[X]); // verify that X was decremented
    }

    [Fact]
    public void LdY()
    {
        LoadProgram("LD r8, Y");
        _cpu.Data[0xc0] = 0x15;
        _cpu.Data[Y] = 0xc0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x15, _cpu.Data[R8]);
        Assert.Equal(0xc0, _cpu.Data[Y]); // verify that Y was unchanged
    }

    [Fact]
    public void LdYPostIncrement()
    {
        LoadProgram("LD r3, Y+");
        _cpu.Data[0xc0] = 0x15;
        _cpu.Data[Y] = 0xc0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x15, _cpu.Data[R3]);
        Assert.Equal(0xc1, _cpu.Data[Y]); // verify that Y was incremented
    }

    [Fact]
    public void LdYPreDecrement()
    {
        LoadProgram("LD r0, -Y");
        _cpu.Data[0x98] = 0x22;
        _cpu.Data[Y] = 0x99;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x22, _cpu.Data[R0]);
        Assert.Equal(0x98, _cpu.Data[Y]); // verify that Y was decremented
    }

    [Fact]
    public void LddY()
    {
        LoadProgram("LDD r4, Y+2");
        _cpu.Data[0x82] = 0x33;
        _cpu.Data[Y] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x33, _cpu.Data[R4]);
        Assert.Equal(0x80, _cpu.Data[Y]); // verify that Y was unchanged
    }

    [Fact]
    public void LdZ()
    {
        LoadProgram("LD r5, Z");
        _cpu.Data[0xcc] = 0xf5;
        _cpu.Data[Z] = 0xcc;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0xf5, _cpu.Data[R5]);
        Assert.Equal(0xcc, _cpu.Data[Z]); // verify that Z was unchanged
    }

    [Fact]
    public void LdZPostIncrement()
    {
        LoadProgram("LD r7, Z+");
        _cpu.Data[0xc0] = 0x25;
        _cpu.Data[Z] = 0xc0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x25, _cpu.Data[R7]);
        Assert.Equal(0xc1, _cpu.Data[Z]); // verify that Z was incremented
    }

    [Fact]
    public void LdZPreDecrement()
    {
        LoadProgram("LD r0, -Z");
        _cpu.Data[0x9e] = 0x66;
        _cpu.Data[Z] = 0x9f;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x66, _cpu.Data[R0]);
        Assert.Equal(0x9e, _cpu.Data[Z]); // verify that Z was decremented
    }

    [Fact]
    public void LddZ()
    {
        LoadProgram("LDD r15, Z+31");
        _cpu.Data[0x9f] = 0x33;
        _cpu.Data[Z] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x33, _cpu.Data[15]);
        Assert.Equal(0x80, _cpu.Data[Z]); // verify that Z was unchanged
    }

    [Fact]
    public void LdXUsesRampXForExtendedDataAddress()
    {
        _cpu = new Cpu(new byte[0x10000], 0x30000);
        LoadProgram("LD r5, X");
        _cpu.Data[RAMPX] = 2;
        _cpu.SetUint16(X, 0x1234);
        _cpu.Data[0x21234] = 0x7a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x7a, _cpu.Data[R5]);
        Assert.Equal(2, _cpu.Data[RAMPX]);
        Assert.Equal(0x1234, _cpu.GetUint16(X));
    }

    [Fact]
    public void LdXPreDecrementBorrowsIntoRampX()
    {
        _cpu = new Cpu(new byte[0x10000], 0x30000);
        LoadProgram("LD r5, -X");
        _cpu.Data[RAMPX] = 2;
        _cpu.SetUint16(X, 0);
        _cpu.Data[0x1ffff] = 0x6b;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x6b, _cpu.Data[R5]);
        Assert.Equal(1, _cpu.Data[RAMPX]);
        Assert.Equal(0xffff, _cpu.GetUint16(X));
    }

    [Fact]
    public void LddYDisplacementCarriesIntoRampYWithoutChangingPointer()
    {
        _cpu = new Cpu(new byte[0x10000], 0x30000);
        LoadProgram("LDD r5, Y+31");
        _cpu.Data[RAMPY] = 0;
        _cpu.SetUint16(Y, 0xfff0);
        _cpu.Data[0x1000f] = 0x5c;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x5c, _cpu.Data[R5]);
        Assert.Equal(0, _cpu.Data[RAMPY]);
        Assert.Equal(0xfff0, _cpu.GetUint16(Y));
    }

    [Fact]
    public void LdsAndStsUseRampDForExtendedDataAddress()
    {
        _cpu = new Cpu(new byte[0x10000], 0x30000);
        LoadProgram("LDS r5, 0x1234", "STS 0x5678, r6");
        _cpu.Data[RAMPD] = 2;
        _cpu.Data[0x21234] = 0x4d;
        _cpu.Data[R6] = 0xa7;
        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x4d, _cpu.Data[R5]);
        Assert.Equal(0xa7, _cpu.Data[0x25678]);
    }

    [Fact]
    public void LdsAndStsCanUseConfiguredDirectAddressRampRegister()
    {
        _cpu = new Cpu(new byte[0x10000], 0x30000, directDataRampRegister: RAMPZ);
        LoadProgram("LDS r5, 0x1234", "STS 0x5678, r6");
        _cpu.Data[RAMPD] = 1;
        _cpu.Data[RAMPZ] = 2;
        _cpu.Data[0x21234] = 0x4d;
        _cpu.Data[R6] = 0xa7;

        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);

        Assert.Equal(0x4d, _cpu.Data[R5]);
        Assert.Equal(0xa7, _cpu.Data[0x25678]);
        Assert.Equal(0, _cpu.Data[0x15678]);
    }

    [Fact]
    public void DataAddressWindowMapsIndirectAndDirectAccessesButKeepsLogicalPointers()
    {
        _cpu = new Cpu(new byte[0x10000], 0xd70000);
        LoadProgram("LD r5, X", "ST Y+, r6", "LD r7, Z", "LDS r8, 0xb103");
        _cpu.ConfigureDataAddressWindow(0x02b100, 0x02b1ff, 0xd6b100);
        _cpu.Data[RAMPX] = 2;
        _cpu.Data[RAMPY] = 2;
        _cpu.Data[RAMPZ] = 2;
        _cpu.Data[RAMPD] = 2;
        _cpu.SetUint16(X, 0xb100);
        _cpu.SetUint16(Y, 0xb101);
        _cpu.SetUint16(Z, 0xb102);
        _cpu.Data[0xd6b100] = 0x51;
        _cpu.Data[0xd6b102] = 0x72;
        _cpu.Data[0xd6b103] = 0x83;
        _cpu.Data[R6] = 0xa6;

        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);

        Assert.Equal(0x51, _cpu.Data[R5]);
        Assert.Equal(0xa6, _cpu.Data[0xd6b101]);
        Assert.Equal(0x72, _cpu.Data[R7]);
        Assert.Equal(0x83, _cpu.Data[R8]);
        Assert.Equal(2, _cpu.Data[RAMPY]);
        Assert.Equal(0xb102, _cpu.GetUint16(Y));
        Assert.Equal(0, _cpu.Data[0x02b101]);
    }

    [Fact]
    public void Ldi()
    {
        LoadProgram("LDI r28, 0xff");
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0xff, _cpu.Data[Y]);
    }

    [Fact]
    public void Lds()
    {
        LoadProgram("LDS r5, 0x150");
        _cpu.Data[0x150] = 0x7a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x7a, _cpu.Data[R5]);
    }

    [Fact]
    public void Lpm()
    {
        LoadProgram("LPM");
        _cpu.SetProgWord(0x40, 0xa0);
        _cpu.Data[Z] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
        Assert.Equal(0xa0, _cpu.Data[R0]);
        Assert.Equal(0x80, _cpu.Data[Z]); // verify that Z was unchanged
    }

    [Fact]
    public void LpmRegister()
    {
        LoadProgram("LPM r2, Z");
        _cpu.SetProgWord(0x40, 0xa0);
        _cpu.Data[Z] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
        Assert.Equal(0xa0, _cpu.Data[R2]);
        Assert.Equal(0x80, _cpu.Data[Z]); // verify that Z was unchanged
    }

    [Fact]
    public void LpmPostIncrement()
    {
        LoadProgram("LPM r1, Z+");
        _cpu.SetProgWord(0x40, 0xa0);
        _cpu.Data[Z] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
        Assert.Equal(0xa0, _cpu.Data[R1]);
        Assert.Equal(0x81, _cpu.Data[Z]); // verify that Z was incremented
    }

    [Fact]
    public void Lsr()
    {
        LoadProgram("LSR r7");
        _cpu.Data[R7] = 0x45;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x22, _cpu.Data[R7]);
        Assert.Equal(SREG_S | SREG_V | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Mov()
    {
        LoadProgram("MOV r7, r8");
        _cpu.Data[R8] = 0x45;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x45, _cpu.Data[R7]);
    }

    [Fact]
    public void Movw()
    {
        LoadProgram("MOVW r26, r22");
        _cpu.Data[R22] = 0x45;
        _cpu.Data[R23] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x45, _cpu.Data[X]);
        Assert.Equal(0x9a, _cpu.Data[R27]);
    }

    [Fact]
    public void Mul()
    {
        LoadProgram("MUL r5, r6");
        _cpu.Data[R5] = 100;
        _cpu.Data[R6] = 5;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(500, _cpu.GetUint16(0));
        Assert.Equal(0, _cpu.Data[SREG]);
    }

    [Fact]
    public void MulSetsCarryFlagForLargeResult()
    {
        LoadProgram("MUL r5, r6");
        _cpu.Data[R5] = 200;
        _cpu.Data[R6] = 200;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(40000, _cpu.GetUint16(0));
        Assert.Equal(SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void MulSetsZeroFlag()
    {
        LoadProgram("MUL r0, r1");
        _cpu.Data[R0] = 0;
        _cpu.Data[R1] = 9;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0, _cpu.GetUint16(0));
        Assert.Equal(SREG_Z, _cpu.Data[SREG]);
    }

    [Fact]
    public void Muls()
    {
        LoadProgram("MULS r18, r19");
        _cpu.Data[R18] = unchecked((byte)-5);
        _cpu.Data[R19] = 100;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(-500, _cpu.GetInt16(0));
        Assert.Equal(SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Mulsu()
    {
        LoadProgram("MULSU r16, r17");
        _cpu.Data[R16] = unchecked((byte)-5);
        _cpu.Data[R17] = 200;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(-1000, _cpu.GetInt16(0));
        Assert.Equal(SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Neg()
    {
        LoadProgram("NEG r20");
        _cpu.Data[R20] = 0x56;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0xaa, _cpu.Data[R20]);
        // H = R3 | Rd3 (result 0xaa has bit 3 set), per instruction set manual section 84.2
        Assert.Equal(SREG_H | SREG_S | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void NegOfZeroSetsZeroClearsCarry()
    {
        LoadProgram("NEG r20");
        _cpu.Data[R20] = 0;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0, _cpu.Data[R20]);
        Assert.Equal(SREG_Z, _cpu.Data[SREG]);
    }

    [Fact]
    public void NegOf0x80SetsOverflow()
    {
        LoadProgram("NEG r20");
        _cpu.Data[R20] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x80, _cpu.Data[R20]); // $80 is left unchanged
        // V set (two's complement overflow), S = N ^ V = 0
        Assert.Equal(SREG_V | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Fmulsu()
    {
        LoadProgram("FMULSU r16, r17");
        _cpu.Data[R16] = unchecked((byte)-2);
        _cpu.Data[R17] = 100;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(-400, _cpu.GetInt16(0)); // (-2 * 100) << 1
        Assert.Equal(SREG_C, _cpu.Data[SREG]); // C = bit 15 of the product before the shift
    }

    [Fact]
    public void FmulsuZeroResultSetsZeroFlag()
    {
        LoadProgram("FMULSU r16, r17");
        _cpu.Data[R16] = 0;
        _cpu.Data[R17] = 200;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0, _cpu.GetUint16(0));
        Assert.Equal(SREG_Z, _cpu.Data[SREG]);
    }

    [Fact]
    public void Nop()
    {
        LoadProgram("NOP");
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
    }

    [Fact]
    public void Out()
    {
        LoadProgram("OUT 0x3f, r1");
        _cpu.Data[R1] = 0x5a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[0x5f]);
    }

    [Fact]
    public void Pop()
    {
        LoadProgram("POP r26");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xff;
        _cpu.Data[0x100] = 0x1a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x1a, _cpu.Data[X]);
        Assert.Equal(0x100, _cpu.GetUint16(SP));
    }

    [Fact]
    public void PopReadsThroughTheDataBusHook()
    {
        LoadProgram("POP r26");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xff;
        _cpu.ReadHooks[0x100] = _ => 0x5a;

        AvrInstruction.Execute(_cpu);

        Assert.Equal(0x5a, _cpu.Data[X]);
    }

    [Fact]
    public void Push()
    {
        LoadProgram("PUSH r11");
        _cpu.Data[11] = 0x2a;
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xff;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x2a, _cpu.Data[0xff]);
        Assert.Equal(0xfe, _cpu.GetUint16(SP));
    }

    [Fact]
    public void PushWritesThroughTheDataBusHook()
    {
        LoadProgram("PUSH r11");
        _cpu.Data[11] = 0x2a;
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xff;
        byte? observed = null;
        _cpu.WriteHooks[0xff] = (value, _, _, _) =>
        {
            observed = value;
            return false;
        };

        AvrInstruction.Execute(_cpu);

        Assert.Equal((byte?)0x2a, observed);
        Assert.Equal(0x2a, _cpu.Data[0xff]);
    }

    [Fact]
    public void RcallForward()
    {
        LoadProgram("RCALL 6");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(4, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
        Assert.Equal(1, _cpu.GetUint16(0x80)); // RET address
        Assert.Equal(0x7e, _cpu.Data[SP]); // SP should decrement by 2
    }

    [Fact]
    public void RcallBackward()
    {
        LoadProgram("NOP", "RCALL -4");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x80;
        AvrInstruction.Execute(_cpu);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0, _cpu.PC);
        Assert.Equal(4, _cpu.Cycles); // 1 for NOP, 3 for RCALL
        Assert.Equal(2, _cpu.GetUint16(0x80)); // RET address
        Assert.Equal(0x7e, _cpu.Data[SP]); // SP should decrement by 2
    }

    [Fact]
    public void RcallPushesThreeByteReturnAddressOnLargeFlash()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("RCALL 6");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x80;
        _cpu.SetUint16(Z, 0x2020);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(4, _cpu.PC);
        Assert.Equal(4, _cpu.Cycles);
        Assert.Equal(1, _cpu.GetUint16(0x80)); // RET address
        Assert.Equal(0x7d, _cpu.Data[SP]); // SP should decrement by 3
    }

    [Fact]
    public void Ret()
    {
        LoadProgram("RET");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x90;
        _cpu.Data[0x92] = 16;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(16, _cpu.PC);
        Assert.Equal(4, _cpu.Cycles);
        Assert.Equal(0x92, _cpu.Data[SP]); // SP should increment by 2
    }

    [Fact]
    public void RetOnLargeFlash()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("RET");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x90;
        _cpu.Data[0x91] = 0x1;
        _cpu.Data[0x93] = 0x16;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x10016, _cpu.PC);
        Assert.Equal(5, _cpu.Cycles);
        Assert.Equal(0x93, _cpu.Data[SP]); // SP should increment by 3
    }

    [Fact]
    public void RetOnLargeFlashPreservesAlignedHighPcBits()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("RET");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x90;
        _cpu.Data[0x91] = 0x1;

        AvrInstruction.Execute(_cpu);

        Assert.Equal(0x10000, _cpu.PC);
        Assert.Equal(5, _cpu.Cycles);
        Assert.Equal(0x93, _cpu.Data[SP]);
    }

    [Fact]
    public void SlowRetOnLargeFlashPreservesAlignedHighPcBits()
    {
        _cpu = new Cpu(new byte[0x40000]);
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0x90;
        _cpu.Data[0x91] = 0x1;

        AvrInstruction.ExecuteSlow(_cpu, 0x9508);

        Assert.Equal(0x10000, _cpu.PC);
        Assert.Equal(5, _cpu.Cycles);
        Assert.Equal(0x93, _cpu.Data[SP]);
    }

    [Fact]
    public void Reti()
    {
        LoadProgram("RETI");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xc0;
        _cpu.Data[0xc2] = 200;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(200, _cpu.PC);
        Assert.Equal(4, _cpu.Cycles);
        Assert.Equal(0xc2, _cpu.Data[SP]); // SP should increment by 2
        Assert.Equal(SREG_I, _cpu.Data[SREG]);
    }

    [Fact]
    public void RetiOnLargeFlash()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("RETI");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xc0;
        _cpu.Data[0xc1] = 0x1;
        _cpu.Data[0xc3] = 0x30;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x10030, _cpu.PC);
        Assert.Equal(5, _cpu.Cycles);
        Assert.Equal(0xc3, _cpu.Data[SP]); // SP should increment by 3
        Assert.Equal(SREG_I, _cpu.Data[SREG]);
    }

    [Fact]
    public void RetiOnLargeFlashPreservesAlignedHighPcBits()
    {
        _cpu = new Cpu(new byte[0x40000]);
        LoadProgram("RETI");
        _cpu.Data[SPH] = 0;
        _cpu.Data[SP] = 0xc0;
        _cpu.Data[0xc1] = 0x1;

        AvrInstruction.Execute(_cpu);

        Assert.Equal(0x10000, _cpu.PC);
        Assert.Equal(5, _cpu.Cycles);
        Assert.Equal(0xc3, _cpu.Data[SP]);
        Assert.Equal(SREG_I, _cpu.Data[SREG]);
    }

    [Fact]
    public void Rjmp()
    {
        LoadProgram("RJMP 2");
        AvrInstruction.Execute(_cpu);
        Assert.Equal(2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
    }

    [Fact]
    public void Ror()
    {
        LoadProgram("ROR r0");
        _cpu.Data[R0] = 0x11;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x08, _cpu.Data[R0]); // r0 should be right-shifted
        Assert.Equal(SREG_S | SREG_V | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void SbcWhenCarryIsOnAndResultOverflows()
    {
        LoadProgram("SBC r0, r1");
        _cpu.Data[R0] = 0;
        _cpu.Data[R1] = 10;
        _cpu.Data[SREG] = SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(245, _cpu.Data[R0]);
        Assert.Equal(SREG_H | SREG_S | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Sbci()
    {
        LoadProgram("SBCI r23, 3");
        _cpu.Data[R23] = 3;
        _cpu.Data[SREG] = SREG_I | SREG_C;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(SREG_I | SREG_H | SREG_S | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Sbi()
    {
        LoadProgram("SBI 0x0c, 5");
        _cpu.Data[0x2c] = 0b00001111;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0b00101111, _cpu.Data[0x2c]);
    }

    [Fact]
    public void SbisWhenBitIsClear()
    {
        LoadProgram("SBIS 0x0c, 5");
        _cpu.Data[0x2c] = 0b00001111;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
    }

    [Fact]
    public void SbisWhenBitIsSet()
    {
        LoadProgram("SBIS 0x0c, 5");
        _cpu.Data[0x2c] = 0b00101111;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
    }

    [Fact]
    public void SbisWhenBitIsSetFollowedByTwoWordInstruction()
    {
        LoadProgram("SBIS 0x0c, 5", "CALL 0xb8");
        _cpu.Data[0x2c] = 0b00101111;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(3, _cpu.PC);
        Assert.Equal(3, _cpu.Cycles);
    }

    [Fact]
    public void StX()
    {
        LoadProgram("ST X, r1");
        _cpu.Data[R1] = 0x5a;
        _cpu.Data[X] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[0x9a]);
        Assert.Equal(0x9a, _cpu.Data[X]); // verify that X was unchanged
    }

    [Fact]
    public void StXPostIncrement()
    {
        LoadProgram("ST X+, r1");
        _cpu.Data[R1] = 0x5a;
        _cpu.Data[X] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[0x9a]);
        Assert.Equal(0x9b, _cpu.Data[X]); // verify that X was incremented
    }

    [Fact]
    public void StXPreDecrement()
    {
        LoadProgram("ST -X, r17");
        _cpu.Data[R17] = 0x88;
        _cpu.Data[X] = 0x99;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x88, _cpu.Data[0x98]);
        Assert.Equal(0x98, _cpu.Data[X]); // verify that X was decremented
    }

    [Fact]
    public void StY()
    {
        LoadProgram("ST Y, r2");
        _cpu.Data[R2] = 0x5b;
        _cpu.Data[Y] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x5b, _cpu.Data[0x9a]);
        Assert.Equal(0x9a, _cpu.Data[Y]); // verify that Y was unchanged
    }

    [Fact]
    public void StYPostIncrement()
    {
        LoadProgram("ST Y+, r1");
        _cpu.Data[R1] = 0x5a;
        _cpu.Data[Y] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[0x9a]);
        Assert.Equal(0x9b, _cpu.Data[Y]); // verify that Y was incremented
    }

    [Fact]
    public void StYPreDecrement()
    {
        LoadProgram("ST -Y, r1");
        _cpu.Data[R1] = 0x5a;
        _cpu.Data[Y] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[0x99]);
        Assert.Equal(0x99, _cpu.Data[Y]); // verify that Y was decremented
    }

    [Fact]
    public void StdY()
    {
        LoadProgram("STD Y+17, r0");
        _cpu.Data[R0] = 0xba;
        _cpu.Data[Y] = 0x9a;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0xba, _cpu.Data[0x9a + 17]);
        Assert.Equal(0x9a, _cpu.Data[Y]); // verify that Y was unchanged
    }

    [Fact]
    public void StZ()
    {
        LoadProgram("ST Z, r16");
        _cpu.Data[R16] = 0xdf;
        _cpu.Data[Z] = 0x40;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0xdf, _cpu.Data[0x40]);
        Assert.Equal(0x40, _cpu.Data[Z]); // verify that Z was unchanged
    }

    [Fact]
    public void StZPostIncrement()
    {
        LoadProgram("ST Z+, r0");
        _cpu.Data[R0] = 0x55;
        _cpu.SetUint16(Z, 0x155);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x55, _cpu.Data[0x155]);
        Assert.Equal(0x156, _cpu.GetUint16(Z)); // verify that Z was incremented
    }

    [Fact]
    public void StZPostIncrementCarriesIntoRampZ()
    {
        _cpu = new Cpu(new byte[0x10000], 0x30000);
        LoadProgram("ST Z+, r0");
        _cpu.Data[R0] = 0x55;
        _cpu.Data[RAMPZ] = 1;
        _cpu.SetUint16(Z, 0xffff);
        AvrInstruction.Execute(_cpu);
        Assert.Equal(0x55, _cpu.Data[0x1ffff]);
        Assert.Equal(2, _cpu.Data[RAMPZ]);
        Assert.Equal(0, _cpu.GetUint16(Z));
    }

    [Fact]
    public void StZPreDecrement()
    {
        LoadProgram("ST -Z, r16");
        _cpu.Data[R16] = 0x5a;
        _cpu.Data[Z] = 0xff;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[0xfe]);
        Assert.Equal(0xfe, _cpu.Data[Z]); // verify that Z was decremented
    }

    [Fact]
    public void StdZ()
    {
        LoadProgram("STD Z+1, r0");
        _cpu.Data[R0] = 0xcc;
        _cpu.Data[Z] = 0x50;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0xcc, _cpu.Data[0x51]);
        Assert.Equal(0x50, _cpu.Data[Z]); // verify that Z was unchanged
    }

    [Fact]
    public void Sts()
    {
        LoadProgram("STS 0x151, r31");
        _cpu.Data[R31] = 0x80;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(2, _cpu.PC);
        Assert.Equal(2, _cpu.Cycles);
        Assert.Equal(0x80, _cpu.Data[0x151]);
    }

    [Fact]
    public void SubWhenResultOverflows()
    {
        LoadProgram("SUB r0, r1");
        _cpu.Data[R0] = 0;
        _cpu.Data[R1] = 10;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(246, _cpu.Data[R0]);
        Assert.Equal(SREG_S | SREG_N | SREG_C, _cpu.Data[SREG]);
    }

    [Fact]
    public void Swap()
    {
        LoadProgram("SWAP r1");
        _cpu.Data[R1] = 0xa5;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0x5a, _cpu.Data[R1]);
    }

    [Fact]
    public void WdrCallsOnWatchdogReset()
    {
        LoadProgram("WDR");
        var called = false;
        _cpu.OnWatchdogReset = () => called = true;
        Assert.False(called);
        AvrInstruction.Execute(_cpu);
        Assert.True(called);
    }

    [Fact]
    public void Xch()
    {
        LoadProgram("XCH Z, r21");
        _cpu.Data[R21] = 0xa1;
        _cpu.Data[Z] = 0x50;
        _cpu.Data[0x50] = 0xb9;
        AvrInstruction.Execute(_cpu);
        Assert.Equal(1, _cpu.PC);
        Assert.Equal(1, _cpu.Cycles);
        Assert.Equal(0xb9, _cpu.Data[R21]);
        Assert.Equal(0xa1, _cpu.Data[0x50]);
    }
}
