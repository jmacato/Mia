#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Security.Cryptography;
using Mia.Emulator;

double seconds = ParseDouble("--seconds=", 20);
long warmupInstructions = ParseLong("--warmup=", 290_000_000);
int batchSize = checked((int)ParseLong("--batch=", 262_144));
if (seconds <= 0 || warmupInstructions < 0 || batchSize <= 0)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/bench_mia_paced.cs -- " +
        "[--seconds=N] [--warmup=N] [--batch=N] [--root=PATH]");
    return 2;
}

string root = args.FirstOrDefault(
    value => value.StartsWith("--root=", StringComparison.Ordinal)) is { } rootText
        ? Path.GetFullPath(rootText["--root=".Length..])
        : Directory.GetCurrentDirectory();
byte[] firmware = File.ReadAllBytes(Path.Combine(root, "flat.bin"));
using var machine = new MiaMachine(
    firmware,
    File.ReadAllBytes(Path.Combine(root, "images", "T68i_Full_GDFS.raw")),
    File.ReadAllBytes(Path.Combine(
        root,
        "images",
        "t68i_R8A015_125326_Modem.bih")),
    Convert.FromHexString("321A065432100654"),
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

while (machine.ExecutedInstructions < warmupInstructions && !machine.IsStopped)
{
    machine.RunInteractiveWorkItems(batchSize);
}

long startInstructions = machine.ExecutedInstructions;
long startSkipped = machine.IdleFastForwardedInstructions;
long startArm = machine.Modem!.Instructions;
long startCycles = machine.Cycles;
var pacer = new RealTimePacer();
pacer.Reanchor(machine.Cycles);
var process = Process.GetCurrentProcess();
TimeSpan cpuStart = process.TotalProcessorTime;
var wall = Stopwatch.StartNew();
while (wall.Elapsed.TotalSeconds < seconds && !machine.IsStopped)
{
    machine.RunInteractiveWorkItems(batchSize);
    pacer.Pace(machine.Cycles, CancellationToken.None);
}
wall.Stop();
process.Refresh();
TimeSpan cpu = process.TotalProcessorTime - cpuStart;

Console.WriteLine(
    $"Firmware SHA-256:   " +
    Convert.ToHexString(SHA256.HashData(firmware)).ToLowerInvariant());
Console.WriteLine($"Wall time:          {wall.Elapsed.TotalSeconds:f3} s");
Console.WriteLine($"Process CPU:        {cpu.TotalSeconds:f3} s");
Console.WriteLine(
    $"One-core CPU:       " +
    $"{cpu.TotalSeconds / wall.Elapsed.TotalSeconds * 100:f2}%");
Console.WriteLine(
    $"ASIC cycles:        {machine.Cycles - startCycles:n0}");
Console.WriteLine(
    $"AVR instructions:   {machine.ExecutedInstructions - startInstructions:n0}");
Console.WriteLine(
    $"AVR fast-forwarded: {machine.IdleFastForwardedInstructions - startSkipped:n0}");
Console.WriteLine(
    $"ARM instructions:   {machine.Modem.Instructions - startArm:n0}");
Console.WriteLine(
    $"Frame SHA-256:      " +
    Convert.ToHexString(SHA256.HashData(machine.Frame.Span)).ToLowerInvariant());
Console.WriteLine($"Stopped:            {machine.IsStopped} {machine.StopReason}");
return machine.IsStopped ? 1 : 0;

long ParseLong(string prefix, long fallback) =>
    args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))
        is { } text && long.TryParse(text[prefix.Length..], out long value)
            ? value
            : fallback;

double ParseDouble(string prefix, double fallback) =>
    args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))
        is { } text && double.TryParse(text[prefix.Length..], out double value)
            ? value
            : fallback;
