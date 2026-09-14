// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js instruction.ts)

namespace AvrCore.Execution;

public static partial class AvrInstruction
{
    static void ExecuteElpm(Cpu cpu, int opcode)
    {
        /* ELPM, 1001 0101 1101 1000 */
        int rampz = cpu.Data[0x5b];
        cpu.Data[0] = cpu.ProgBytes[(rampz << 16) | cpu.GetUint16(30)];
        cpu.Cycles += 2;
    }

    static void ExecuteElpmRegister(Cpu cpu, int opcode)
    {
        /* ELPM(REG), 1001 000d dddd 0110 */
        int rampz = cpu.Data[0x5b];
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ProgBytes[(rampz << 16) | cpu.GetUint16(30)];
        cpu.Cycles += 2;
    }

    static void ExecuteElpmPostIncrement(Cpu cpu, int opcode)
    {
        /* ELPM(INC), 1001 000d dddd 0111 */
        int rampz = cpu.Data[0x5b];
        var i = cpu.GetUint16(30);
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ProgBytes[(rampz << 16) | i];
        cpu.SetUint16(30, i + 1);
        if (i == 0xffff)
        {
            var banks = cpu.ProgBytes.Length >> 16;
            cpu.Data[0x5b] = (byte)(banks > 0 ? (rampz + 1) % banks : 0);
        }
        cpu.Cycles += 2;
    }

    static void ExecuteIn(Cpu cpu, int opcode)
    {
        /* IN, 1011 0AAd dddd AAAA */
        var i = cpu.ReadData(((opcode & 0xf) | ((opcode & 0x600) >> 5)) + 32);
        cpu.Data[(opcode & 0x1f0) >> 4] = i;
    }

    static void ExecuteLac(Cpu cpu, int opcode)
    {
        /* LAC, 1001 001r rrrr 0110 */
        var r = (opcode & 0x1f0) >> 4;
        int clear = cpu.Data[r];
        var address = cpu.GetDataAddress(30, 0x5b);
        var value = cpu.ReadData(address);
        cpu.WriteData(address, (byte)(value & (255 - clear)));
        cpu.Data[r] = value;
    }

    static void ExecuteLas(Cpu cpu, int opcode)
    {
        /* LAS, 1001 001r rrrr 0101 */
        var r = (opcode & 0x1f0) >> 4;
        int set = cpu.Data[r];
        var address = cpu.GetDataAddress(30, 0x5b);
        var value = cpu.ReadData(address);
        cpu.WriteData(address, (byte)(value | set));
        cpu.Data[r] = value;
    }

    static void ExecuteLat(Cpu cpu, int opcode)
    {
        /* LAT, 1001 001r rrrr 0111 */
        int r = cpu.Data[(opcode & 0x1f0) >> 4];
        var address = cpu.GetDataAddress(30, 0x5b);
        var R = cpu.ReadData(address);
        cpu.WriteData(address, (byte)(r ^ R));
        cpu.Data[(opcode & 0x1f0) >> 4] = R;
    }

    static void ExecuteLdi(Cpu cpu, int opcode)
    {
        /* LDI, 1110 KKKK dddd KKKK */
        cpu.Data[((opcode & 0xf0) >> 4) + 16] = (byte)((opcode & 0xf) | ((opcode & 0xf00) >> 4));
    }

    static void ExecuteLds(Cpu cpu, int opcode)
    {
        /* LDS, 1001 000d dddd 0000 kkkk kkkk kkkk kkkk */
        cpu.Cycles++;
        var value = cpu.ReadData(cpu.GetDirectDataAddress(cpu.GetProgWord(cpu.PC + 1)));
        cpu.Data[(opcode & 0x1f0) >> 4] = value;
        cpu.PC++;
    }

    static void ExecuteLdx(Cpu cpu, int opcode)
    {
        /* LDX, 1001 000d dddd 1100 */
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(cpu.GetDataAddress(26, 0x59));
    }

    static void ExecuteLdxPostIncrement(Cpu cpu, int opcode)
    {
        /* LDX(INC), 1001 000d dddd 1101 */
        var x = cpu.GetDataAddress(26, 0x59);
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(x);
        cpu.SetDataAddress(26, 0x59, x + 1);
    }

    static void ExecuteLdxPreDecrement(Cpu cpu, int opcode)
    {
        /* LDX(DEC), 1001 000d dddd 1110 */
        var x = cpu.GetDataAddress(26, 0x59) - 1;
        cpu.SetDataAddress(26, 0x59, x);
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(x);
    }

    static void ExecuteLdy(Cpu cpu, int opcode)
    {
        /* LDY, 1000 000d dddd 1000 */
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(cpu.GetDataAddress(28, 0x5a));
    }

    static void ExecuteLdyPostIncrement(Cpu cpu, int opcode)
    {
        /* LDY(INC), 1001 000d dddd 1001 */
        var y = cpu.GetDataAddress(28, 0x5a);
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(y);
        cpu.SetDataAddress(28, 0x5a, y + 1);
    }

    static void ExecuteLdyPreDecrement(Cpu cpu, int opcode)
    {
        /* LDY(DEC), 1001 000d dddd 1010 */
        var y = cpu.GetDataAddress(28, 0x5a) - 1;
        cpu.SetDataAddress(28, 0x5a, y);
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(y);
    }

    static void ExecuteLddy(Cpu cpu, int opcode)
    {
        /* LDDY, 10q0 qq0d dddd 1qqq */
        cpu.Cycles++;
        var displacement = (opcode & 7) | ((opcode & 0xc00) >> 7) | ((opcode & 0x2000) >> 8);
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(cpu.GetDataAddress(28, 0x5a, displacement));
    }

    static void ExecuteLdz(Cpu cpu, int opcode)
    {
        /* LDZ, 1000 000d dddd 0000 */
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(cpu.GetDataAddress(30, 0x5b));
    }

    static void ExecuteLdzPostIncrement(Cpu cpu, int opcode)
    {
        /* LDZ(INC), 1001 000d dddd 0001 */
        var z = cpu.GetDataAddress(30, 0x5b);
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(z);
        cpu.SetDataAddress(30, 0x5b, z + 1);
    }

    static void ExecuteLdzPreDecrement(Cpu cpu, int opcode)
    {
        /* LDZ(DEC), 1001 000d dddd 0010 */
        var z = cpu.GetDataAddress(30, 0x5b) - 1;
        cpu.SetDataAddress(30, 0x5b, z);
        cpu.Cycles++;
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(z);
    }

    static void ExecuteLddz(Cpu cpu, int opcode)
    {
        /* LDDZ, 10q0 qq0d dddd 0qqq */
        cpu.Cycles++;
        var displacement = (opcode & 7) | ((opcode & 0xc00) >> 7) | ((opcode & 0x2000) >> 8);
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(cpu.GetDataAddress(30, 0x5b, displacement));
    }

    static void ExecuteLpm(Cpu cpu, int opcode)
    {
        /* LPM, 1001 0101 1100 1000 */
        cpu.Data[0] = cpu.ProgBytes[cpu.GetUint16(30)];
        cpu.Cycles += 2;
    }

    static void ExecuteLpmRegister(Cpu cpu, int opcode)
    {
        /* LPM(REG), 1001 000d dddd 0100 */
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ProgBytes[cpu.GetUint16(30)];
        cpu.Cycles += 2;
    }

    static void ExecuteLpmPostIncrement(Cpu cpu, int opcode)
    {
        /* LPM(INC), 1001 000d dddd 0101 */
        var i = cpu.GetUint16(30);
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ProgBytes[i];
        cpu.SetUint16(30, i + 1);
        cpu.Cycles += 2;
    }

    static void ExecuteMov(Cpu cpu, int opcode)
    {
        /* MOV, 0010 11rd dddd rrrr */
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.Data[(opcode & 0xf) | ((opcode & 0x200) >> 5)];
    }

    static void ExecuteMovw(Cpu cpu, int opcode)
    {
        /* MOVW, 0000 0001 dddd rrrr */
        var r2 = 2 * (opcode & 0xf);
        var d2 = 2 * ((opcode & 0xf0) >> 4);
        cpu.Data[d2] = cpu.Data[r2];
        cpu.Data[d2 + 1] = cpu.Data[r2 + 1];
    }

    static void ExecuteOut(Cpu cpu, int opcode)
    {
        /* OUT, 1011 1AAr rrrr AAAA */
        cpu.WriteData(((opcode & 0xf) | ((opcode & 0x600) >> 5)) + 32, cpu.Data[(opcode & 0x1f0) >> 4]);
    }

    static void ExecutePop(Cpu cpu, int opcode)
    {
        /* POP, 1001 000d dddd 1111 */
        var value = cpu.GetUint16(93) + 1;
        cpu.SetUint16(93, value);
        cpu.Data[(opcode & 0x1f0) >> 4] = cpu.ReadData(value);
        cpu.Cycles++;
    }

    static void ExecutePush(Cpu cpu, int opcode)
    {
        /* PUSH, 1001 001d dddd 1111 */
        var value = cpu.GetUint16(93);
        cpu.WriteData(value, cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.SetUint16(93, value - 1);
        cpu.Cycles++;
    }

    static void ExecuteSts(Cpu cpu, int opcode)
    {
        /* STS, 1001 001d dddd 0000 kkkk kkkk kkkk kkkk */
        var value = cpu.Data[(opcode & 0x1f0) >> 4];
        var addr = cpu.GetDirectDataAddress(cpu.GetProgWord(cpu.PC + 1));
        cpu.WriteData(addr, value);
        cpu.PC++;
        cpu.Cycles++;
    }

    static void ExecuteStx(Cpu cpu, int opcode)
    {
        /* STX, 1001 001r rrrr 1100 */
        cpu.WriteData(cpu.GetDataAddress(26, 0x59), cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.Cycles++;
    }

    static void ExecuteStxPostIncrement(Cpu cpu, int opcode)
    {
        /* STX(INC), 1001 001r rrrr 1101 */
        var x = cpu.GetDataAddress(26, 0x59);
        cpu.WriteData(x, cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.SetDataAddress(26, 0x59, x + 1);
        cpu.Cycles++;
    }

    static void ExecuteStxPreDecrement(Cpu cpu, int opcode)
    {
        /* STX(DEC), 1001 001r rrrr 1110 */
        var i = cpu.Data[(opcode & 0x1f0) >> 4];
        var x = cpu.GetDataAddress(26, 0x59) - 1;
        cpu.SetDataAddress(26, 0x59, x);
        cpu.WriteData(x, i);
        cpu.Cycles++;
    }

    static void ExecuteSty(Cpu cpu, int opcode)
    {
        /* STY, 1000 001r rrrr 1000 */
        cpu.WriteData(cpu.GetDataAddress(28, 0x5a), cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.Cycles++;
    }

    static void ExecuteStyPostIncrement(Cpu cpu, int opcode)
    {
        /* STY(INC), 1001 001r rrrr 1001 */
        var i = cpu.Data[(opcode & 0x1f0) >> 4];
        var y = cpu.GetDataAddress(28, 0x5a);
        cpu.WriteData(y, i);
        cpu.SetDataAddress(28, 0x5a, y + 1);
        cpu.Cycles++;
    }

    static void ExecuteStyPreDecrement(Cpu cpu, int opcode)
    {
        /* STY(DEC), 1001 001r rrrr 1010 */
        var i = cpu.Data[(opcode & 0x1f0) >> 4];
        var y = cpu.GetDataAddress(28, 0x5a) - 1;
        cpu.SetDataAddress(28, 0x5a, y);
        cpu.WriteData(y, i);
        cpu.Cycles++;
    }

    static void ExecuteStdy(Cpu cpu, int opcode)
    {
        /* STDY, 10q0 qq1r rrrr 1qqq */
        var displacement = (opcode & 7) | ((opcode & 0xc00) >> 7) | ((opcode & 0x2000) >> 8);
        cpu.WriteData(cpu.GetDataAddress(28, 0x5a, displacement), cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.Cycles++;
    }

    static void ExecuteStz(Cpu cpu, int opcode)
    {
        /* STZ, 1000 001r rrrr 0000 */
        cpu.WriteData(cpu.GetDataAddress(30, 0x5b), cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.Cycles++;
    }

    static void ExecuteStzPostIncrement(Cpu cpu, int opcode)
    {
        /* STZ(INC), 1001 001r rrrr 0001 */
        var z = cpu.GetDataAddress(30, 0x5b);
        cpu.WriteData(z, cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.SetDataAddress(30, 0x5b, z + 1);
        cpu.Cycles++;
    }

    static void ExecuteStzPreDecrement(Cpu cpu, int opcode)
    {
        /* STZ(DEC), 1001 001r rrrr 0010 */
        var i = cpu.Data[(opcode & 0x1f0) >> 4];
        var z = cpu.GetDataAddress(30, 0x5b) - 1;
        cpu.SetDataAddress(30, 0x5b, z);
        cpu.WriteData(z, i);
        cpu.Cycles++;
    }

    static void ExecuteStdz(Cpu cpu, int opcode)
    {
        /* STDZ, 10q0 qq1r rrrr 0qqq */
        var displacement = (opcode & 7) | ((opcode & 0xc00) >> 7) | ((opcode & 0x2000) >> 8);
        cpu.WriteData(cpu.GetDataAddress(30, 0x5b, displacement), cpu.Data[(opcode & 0x1f0) >> 4]);
        cpu.Cycles++;
    }

    static void ExecuteXch(Cpu cpu, int opcode)
    {
        /* XCH, 1001 001r rrrr 0100 */
        var r = (opcode & 0x1f0) >> 4;
        var val1 = cpu.Data[r];
        var address = cpu.GetDataAddress(30, 0x5b);
        var val2 = cpu.ReadData(address);
        cpu.WriteData(address, val1);
        cpu.Data[r] = val2;
    }
}
