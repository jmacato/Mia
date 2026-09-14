#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using System.Security.Cryptography;
using Mia.Emulator;

var instructionLimit = args.Length > 0 && long.TryParse(args[0], out var parsedLimit)
    ? parsedLimit
    : 300_000_000;
var gdfsPath = args.Length > 1
    ? args[1]
    : "images/T68i_Full_GDFS.compact.raw";
var frameLimit = args.Length > 2 && int.TryParse(args[2], out var parsedFrameLimit)
    ? parsedFrameLimit
    : 25;
var modemPath = "images/t68i_R8A015_125326_Modem.bih";
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes(gdfsPath),
    File.ReadAllBytes(modemPath),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

Console.WriteLine("frame\tinstructions\tasic-ticks\tdelta-ms\tframe-sha256");
var observedVersion = 0;
long previousFrameCycle = 0;
var samples = new Dictionary<(int Pc, byte Process), long>();
while (machine.ExecutedInstructions < instructionLimit &&
       (frameLimit <= 0 || machine.FrameVersion < frameLimit) &&
       !machine.IsStopped)
{
    machine.RunWorkItems(4_096);
    var sample = (machine.Cpu.PC, machine.Cpu.ReadData(0xf606));
    samples[sample] = samples.GetValueOrDefault(sample) + 1;
    var version = machine.FrameVersion;
    if (version == observedVersion)
    {
        continue;
    }

    var cycle = machine.Cycles;
    var deltaMilliseconds = previousFrameCycle == 0
        ? 0
        : (cycle - previousFrameCycle) * 1_000.0 / MiaSystemClock.AsicCyclesPerSecond;
    Console.WriteLine(
        $"{version}\t{machine.ExecutedInstructions}\t{cycle}\t" +
        $"{deltaMilliseconds:F3}\t" +
        Convert.ToHexStringLower(SHA256.HashData(machine.Frame.Span)));
    observedVersion = version;
    previousFrameCycle = cycle;
}

Console.WriteLine(
    $"stopped={machine.IsStopped} reason={machine.StopReason} " +
    $"instructions={machine.ExecutedInstructions} frames={machine.FrameVersion}");
Console.WriteLine(
    $"pc={machine.Cpu.PC:x6} sp={machine.Cpu.SP:x6} " +
    $"process={machine.Cpu.ReadData(0xf606):x2} cycles={machine.Cycles} " +
    $"scheduler={Convert.ToHexStringLower(machine.Cpu.Data.AsSpan(0xf600, 16))}");
Console.WriteLine(
    $"primary-i2c steps={machine.PrimaryI2c.CompletedSteps} " +
    $"transactions={machine.PrimaryI2c.CompletedTransactions} " +
    $"last-address={machine.PrimaryI2c.LastAddress:x2}");
Console.WriteLine(
    $"display-i2c steps={machine.DisplayI2c.CompletedSteps} " +
    $"transactions={machine.DisplayI2c.CompletedTransactions} " +
    $"pixels={machine.Display.PixelWriteCount}");
Console.WriteLine(
    $"flash reads={machine.FlashMemory.ReadCount} writes={machine.FlashMemory.WriteCount} " +
    $"program={machine.FlashMemory.ProgramWordCount} " +
    $"erase={machine.FlashMemory.EraseConfirmCount}");
foreach (var context in AsicRom.GetSchedulerContexts(machine.Cpu)
             .Where(context => context.Process == machine.Cpu.ReadData(0xf606)))
{
    Console.WriteLine(
        $"active-context={context.Address:x6} descriptor={context.DescriptorAddress:x6} " +
        $"saved-pc={context.SavedPc:x6} state={context.State:x2} " +
        $"runnable={context.Runnable}");
}
Console.WriteLine("sampled-hotspots:");
foreach (var (sample, count) in samples.OrderByDescending(pair => pair.Value).Take(20))
{
    Console.WriteLine($"  pc={sample.Pc:x6} process={sample.Process:x2} samples={count}");
}
