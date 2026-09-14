#!/usr/bin/env dotnet
#:project ../src/Arm7Core/Arm7Core.csproj

using System.Diagnostics;
using Arm7Core;

var iterations = args.Length > 0 && long.TryParse(args[0], out var parsed)
    ? parsed
    : 100_000_000;
var bus = new RepeatingThumbBus();
var cpu = new Arm7Tdmi(bus);
cpu.ForceStatus(Arm7Tdmi.ModeSys | 0x20);
cpu.SetGpr(15, 4);
cpu.PrimePipeline(0x46c0, 0x46c0, ArmAccess.Code | ArmAccess.Sequential);

for (var iteration = 0; iteration < 1_000_000; iteration++)
{
    cpu.Step();
}

var stopwatch = Stopwatch.StartNew();
for (long iteration = 0; iteration < iterations; iteration++)
{
    cpu.Step();
}
stopwatch.Stop();

Console.WriteLine($"Instructions: {iterations:n0}");
Console.WriteLine($"Elapsed:      {stopwatch.Elapsed.TotalSeconds:f3} s");
Console.WriteLine($"Throughput:   {iterations / stopwatch.Elapsed.TotalSeconds:n0} instructions/s");
Console.WriteLine($"Bus cycles:   {bus.Cycles:n0}");

sealed class RepeatingThumbBus : IArm7Bus
{
    public long Cycles { get; private set; }

    public uint ReadWord(uint address, ArmAccess access)
    {
        Cycles++;
        return 0x46c046c0;
    }

    public uint ReadHalf(uint address, ArmAccess access)
    {
        Cycles++;
        return 0x46c0;
    }

    public uint ReadByte(uint address, ArmAccess access)
    {
        Cycles++;
        return 0xc0;
    }

    public void WriteWord(uint address, uint value, ArmAccess access) => Cycles++;

    public void WriteHalf(uint address, ushort value, ArmAccess access) => Cycles++;

    public void WriteByte(uint address, byte value, ArmAccess access) => Cycles++;

    public void Idle() => Cycles++;
}
