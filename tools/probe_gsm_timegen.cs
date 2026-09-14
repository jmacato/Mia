#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

var instructionLimit = args.Length > 0 && long.TryParse(args[0], out var parsedLimit)
    ? parsedLimit
    : 100_000_000;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: 30_000_000,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

var samples = new List<DescriptorSample>();
var counts = new Dictionary<(int Port, AsicTimeGeneratorDescriptor Descriptor), long>();
machine.TimeGenerator.DescriptorProgrammed += (port, descriptor) =>
{
    var key = (port, descriptor);
    counts[key] = counts.GetValueOrDefault(key) + 1;
    if (samples.Count < 2_000)
    {
        samples.Add(new(
            machine.ExecutedInstructions,
            machine.Cycles,
            machine.Cpu.PC,
            machine.Cpu.Data[0xf606],
            port,
            descriptor));
    }
};

while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"END i={machine.ExecutedInstructions:n0} c={machine.Cycles:n0} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason} " +
    $"rollovers={machine.TimeGenerator.FrameRolloverCount:n0} " +
    $"descriptors={machine.TimeGenerator.ProgrammedDescriptorCount:n0}");
Console.WriteLine("FIRST DESCRIPTORS");
foreach (var sample in samples.Take(400))
{
    Console.WriteLine(
        $"i={sample.Instructions:n0} c={sample.Cycles:n0} " +
        $"pc={sample.Pc:x6} process={sample.Process:x2} port={sample.Port:x4} " +
        $"bytes={sample.Descriptor.Byte0:x2}{sample.Descriptor.Byte1:x2}{sample.Descriptor.Byte2:x2}");
}
Console.WriteLine("UNIQUE DESCRIPTORS");
foreach (var entry in counts.OrderByDescending(entry => entry.Value)
             .ThenBy(entry => entry.Key.Port)
             .ThenBy(entry => entry.Key.Descriptor.Byte0)
             .Take(500))
{
    Console.WriteLine(
        $"count={entry.Value:n0} port={entry.Key.Port:x4} " +
        $"bytes={entry.Key.Descriptor.Byte0:x2}{entry.Key.Descriptor.Byte1:x2}{entry.Key.Descriptor.Byte2:x2}");
}

readonly record struct DescriptorSample(
    long Instructions,
    long Cycles,
    int Pc,
    byte Process,
    int Port,
    AsicTimeGeneratorDescriptor Descriptor);
