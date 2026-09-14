#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// Temporary ARM modem hot-PC probe. Requires the uncommitted instruction hook.

using Mia.Emulator;

long warmup = args.Length > 0 ? long.Parse(args[0]) : 300_000_000;
long sampleInstructions = args.Length > 1 ? long.Parse(args[1]) : 5_000_000;
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

// Keep the separate DSP protocol experiment from affecting this CPU profile.
machine.Modem!.Bus.SetExperimentalDspFrameStatus(0);
while (machine.ExecutedInstructions < warmup && !machine.IsStopped)
{
    machine.RunWorkItems(262_144);
}

var counts = new Dictionary<uint, long>();
var modeCounts = new Dictionary<uint, long>();
machine.Modem.InstructionExecuting += modem =>
{
    uint pc = modem.CurrentInstructionAddress;
    counts.TryGetValue(pc, out long count);
    counts[pc] = count + 1;
    uint mode = modem.Cpu.CpsrValue & 0x3f;
    modeCounts.TryGetValue(mode, out long modeCount);
    modeCounts[mode] = modeCount + 1;
};

long started = machine.Modem.Instructions;
while (machine.Modem.Instructions - started < sampleInstructions && !machine.IsStopped)
{
    machine.RunWorkItems(262_144);
}

long sampled = machine.Modem.Instructions - started;
Console.WriteLine(
    $"sampled={sampled:n0} arm instructions; avr={machine.ExecutedInstructions:n0}; " +
    $"cycles={machine.Cycles:n0}; sleeping={machine.Modem.IsSleeping}; " +
    $"irq=0x{machine.Modem.Bus.IrqPending:x8}");
Console.WriteLine("CPU modes (low CPSR 6 bits):");
foreach (var pair in modeCounts.OrderByDescending(pair => pair.Value))
{
    Console.WriteLine($"  0x{pair.Key:x2}: {pair.Value:n0} ({pair.Value * 100.0 / sampled:f2}%)");
}
Console.WriteLine("Hot ARM PCs:");
foreach (var pair in counts.OrderByDescending(pair => pair.Value).Take(100))
{
    Console.WriteLine($"  0x{pair.Key:x8}: {pair.Value:n0} ({pair.Value * 100.0 / sampled:f2}%)");
}
