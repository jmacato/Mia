#!/usr/bin/env dotnet
#:project ../src/AvrCore/AvrCore.csproj

using System.Diagnostics;
using AvrCore;

var iterations = args.Length > 0 && long.TryParse(args[0], out var parsed)
    ? parsed
    : 500_000_000;
var hot = args.Contains("--hot", StringComparer.Ordinal);
var program = hot ? new byte[0x800000] : new byte[] { 0x00, 0xc0 };
var cpu = new Cpu(program, hot ? 0x1000000 : 8192, directDataRampRegister: 0x5b);
var opcodes = hot
    ? new[]
    {
        0xe000, 0x2fe0, 0x2ff1, 0x2711, 0x4010, 0xbf2b, 0x0f04, 0x1f15,
        0x0750, 0x5000, 0x1f26, 0x4228, 0x0760, 0x0770, 0xe001, 0xf009,
        0x3040, 0x4f5f, 0x2044, 0xf539, 0x4f6f, 0x4f7f, 0xf5e0, 0xcfb9,
        0x8001, 0x2f28, 0x8012, 0x5f47, 0x0417, 0x1406,
    }
    : [0xc000];

var opcodeIndex = 0;
for (var iteration = 0; iteration < 1_000_000; iteration++)
{
    AvrInstruction.Execute(cpu, opcodes[opcodeIndex]);
    opcodeIndex = opcodeIndex + 1 == opcodes.Length ? 0 : opcodeIndex + 1;
}

var stopwatch = Stopwatch.StartNew();
for (long iteration = 0; iteration < iterations; iteration++)
{
    AvrInstruction.Execute(cpu, opcodes[opcodeIndex]);
    opcodeIndex = opcodeIndex + 1 == opcodes.Length ? 0 : opcodeIndex + 1;
}
stopwatch.Stop();

Console.WriteLine($"Instructions: {iterations:n0}");
Console.WriteLine($"Elapsed:      {stopwatch.Elapsed.TotalSeconds:f3} s");
Console.WriteLine($"Throughput:   {iterations / stopwatch.Elapsed.TotalSeconds:n0} instructions/s");
Console.WriteLine($"PC/cycles:    {cpu.PC}/{cpu.Cycles:n0}");
