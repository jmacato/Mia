#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using Mia.Emulator;

var repoRoot = Directory.GetCurrentDirectory();
var targetInstructions = args.Length > 0 && long.TryParse(args[0], out var parsed)
    ? parsed
    : 50_000_000;
var collectOpcodes = args.Contains("--opcodes", StringComparer.Ordinal);
var collectPcs = args.Contains("--pcs", StringComparer.Ordinal);
var includeModem = !args.Contains("--no-modem", StringComparer.Ordinal);
var coarseParallel = args.Contains("--coarse-parallel", StringComparer.Ordinal);
var reportWorkers = args.Contains("--workers", StringComparer.Ordinal);
var warmupInstructions = args
    .FirstOrDefault(value => value.StartsWith("--warmup=", StringComparison.Ordinal)) is { } warmup &&
    long.TryParse(warmup["--warmup=".Length..], out var parsedWarmup)
        ? parsedWarmup
        : 0;
var batchSize = args
    .FirstOrDefault(value => value.StartsWith("--batch=", StringComparison.Ordinal)) is { } batch &&
    int.TryParse(batch["--batch=".Length..], out var parsedBatch)
        ? parsedBatch
        : 262_144;
var firmwarePath = Path.Combine(repoRoot, "flat.bin");
var gdfsPath = Path.Combine(repoRoot, "images", "T68i_Full_GDFS.raw");
var modemPath = Path.Combine(repoRoot, "images", "t68i_R8A015_125326_Modem.bih");

var firmware = File.ReadAllBytes(firmwarePath);
var gdfs = File.ReadAllBytes(gdfsPath);
var modem = includeModem ? File.ReadAllBytes(modemPath) : [];
using var machine = new MiaMachine(
    firmware,
    gdfs,
    modem,
    Convert.FromHexString("321A065432100654"),
    coreSchedulingMode: coarseParallel
        ? MiaCoreSchedulingMode.CoarseParallel
        : MiaCoreSchedulingMode.CycleLocked);
long asicToModemBytes = 0;
long modemToAsicBytes = 0;
machine.ByteChannels.ByteTransmitted += (channel, _) =>
{
    if (channel == 1)
    {
        asicToModemBytes++;
    }
};
if (machine.Modem is not null)
{
    machine.Modem.Uart1ByteTransmitted += _ => modemToAsicBytes++;
}

while (machine.ExecutedInstructions < warmupInstructions && !machine.IsStopped)
{
    machine.RunWorkItems(batchSize);
}
var measuredStart = machine.ExecutedInstructions;
var measuredEnd = measuredStart + targetInstructions;
var measuredStartArm = machine.Modem?.Instructions ?? 0;
var measuredStartAsicCycles = machine.Cycles;
var measuredStartInterrupts = machine.InterruptController.RaisedCount;
var opcodeCounts = collectOpcodes ? new long[ushort.MaxValue + 1] : null;
var pcCounts = collectPcs ? new long[machine.Cpu.ProgWords] : null;
if (opcodeCounts is not null || pcCounts is not null)
{
    machine.ExecutionObserver = new ExecutionProfileObserver(opcodeCounts, pcCounts);
}
var stopwatch = Stopwatch.StartNew();
while (machine.ExecutedInstructions < measuredEnd && !machine.IsStopped)
{
    machine.RunWorkItems(batchSize);
}

stopwatch.Stop();

var measuredInstructions = machine.ExecutedInstructions - measuredStart;
var rate = measuredInstructions / stopwatch.Elapsed.TotalSeconds;
Console.WriteLine($"Firmware SHA-256: {Convert.ToHexString(SHA256.HashData(firmware)).ToLowerInvariant()}");
Console.WriteLine($"Instructions:       {machine.ExecutedInstructions:n0}");
Console.WriteLine($"Measured:           {measuredInstructions:n0}");
Console.WriteLine(
    $"Measured ARM:       {(machine.Modem?.Instructions ?? 0) - measuredStartArm:n0}");
Console.WriteLine($"Measured ASIC:      {machine.Cycles - measuredStartAsicCycles:n0} cycles");
Console.WriteLine(
    $"Measured IRQs:      {machine.InterruptController.RaisedCount - measuredStartInterrupts:n0}");
Console.WriteLine($"AVR cycles:         {machine.Cpu.Cycles:n0}");
Console.WriteLine($"ASIC ticks:         {machine.Cycles:n0}");
Console.WriteLine(
    $"Idle fast-forward:  {machine.IdleFastForwardCount:n0} grants, " +
    $"{machine.IdleFastForwardedInstructions:n0} instructions");
Console.WriteLine($"ARM instructions:   {machine.Modem?.Instructions ?? 0:n0}");
Console.WriteLine($"ARM cycles:         {machine.Modem?.Cycles ?? 0:n0}");
if (machine.Modem is { } measuredModem)
{
    Console.WriteLine(
        $"ARM state:          pc=0x{measuredModem.CurrentInstructionAddress:x8} " +
        $"sleeping={measuredModem.IsSleeping} irq=0x{measuredModem.Bus.IrqPending:x8}");
}
Console.WriteLine($"LCD frames:         {machine.FrameVersion:n0}");
Console.WriteLine($"Link bytes:         {asicToModemBytes:n0} -> ARM, {modemToAsicBytes:n0} -> AVR");
Console.WriteLine(
    $"Frame SHA-256:      " +
    Convert.ToHexString(SHA256.HashData(machine.Frame.Span)).ToLowerInvariant());
Console.WriteLine($"Elapsed:            {stopwatch.Elapsed.TotalSeconds:f3} s");
Console.WriteLine($"Throughput:         {rate:n0} instructions/s");
Console.WriteLine($"Stopped:            {machine.IsStopped} {machine.StopReason}");
Console.WriteLine(
    $"Flash words:        reset={machine.FlashMemory.ReadArrayCommandCount:n0}, " +
    $"id={machine.FlashMemory.IdentifierCommandCount:n0}, " +
    $"clear={machine.FlashMemory.ClearStatusCommandCount:n0}, " +
    $"program={machine.FlashMemory.ProgramSetupCommandCount:n0}/" +
    $"{machine.FlashMemory.ProgramWordCount:n0}, " +
    $"lock={machine.FlashMemory.LockSetupCommandCount:n0}/" +
    $"{machine.FlashMemory.LockConfirmCount:n0}, " +
    $"erase={machine.FlashMemory.EraseSetupCommandCount:n0}/" +
    $"{machine.FlashMemory.EraseConfirmCount:n0}, " +
    $"other={machine.FlashMemory.OtherWordCount:n0}");
if (reportWorkers)
{
    Console.WriteLine("Workers:");
    foreach (var worker in machine.Workers)
    {
        Console.WriteLine(
            $"  {worker.Name}: thread={worker.ThreadId}, " +
            $"wakes={worker.WakeCount:n0}, " +
            $"work={worker.CompletedWorkCount:n0}, " +
            $"mmio={worker.BoundMmioReadCount:n0}r/" +
            $"{worker.BoundMmioWriteCount:n0}w");
    }
    if (machine.ArmWorkerThreadId is int armThread)
    {
        Console.WriteLine($"  ARM core: thread={armThread}");
        Console.WriteLine(
            $"  core grants: {machine.CoarseGrantCount:n0}, " +
            $"ARM already ready={machine.ArmGrantReadyCount:n0}, " +
            $"catchups={machine.ArmCatchupCount:n0}");
    }
    Console.WriteLine(
        $"Interrupts: raised={machine.InterruptController.RaisedCount:n0}, " +
        $"dispatched={machine.InterruptController.DispatchedCount:n0}, " +
        $"returned={machine.InterruptController.ReturnedCount:n0}");
    Console.WriteLine("Interrupt sources:");
    for (var source = 0; source <= byte.MaxValue; source++)
    {
        var count = machine.InterruptController.GetRaisedCount((byte)source);
        if (count != 0)
        {
            Console.WriteLine(
                $"  0x{source:x2} " +
                $"{AsicInterruptController.GetSourceName((byte)source)}: {count:n0}");
        }
    }
}
if (opcodeCounts is not null)
{
    Console.WriteLine("Hot opcodes:");
    foreach (var (count, opcode) in opcodeCounts
        .Select((count, opcode) => (count, opcode))
        .OrderByDescending(item => item.count)
        .Take(40))
    {
        Console.WriteLine($"  0x{opcode:x4}: {count:n0} ({count * 100.0 / measuredInstructions:f2}%)");
    }
}
if (pcCounts is not null)
{
    Console.WriteLine("Hot firmware PCs:");
    foreach (var (count, pc) in pcCounts
        .Select((count, pc) => (count, pc))
        .OrderByDescending(item => item.count)
        .Take(80))
    {
        Console.WriteLine(
            $"  0x{pc:x6}: {count:n0} " +
            $"({count * 100.0 / measuredInstructions:f2}%) " +
            $"opcode=0x{machine.Cpu.GetProgWord(pc):x4}");
    }
}
sealed class ExecutionProfileObserver(
    long[]? opcodeCounts,
    long[]? pcCounts) : IMiaExecutionObserver
{
    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if ((uint)cpu.PC < (uint)cpu.ProgWords &&
            cpu.PC < AsicRom.StartWord)
        {
            if (opcodeCounts is not null)
            {
                opcodeCounts[cpu.GetProgWord(cpu.PC)]++;
            }
            if (pcCounts is not null)
            {
                pcCounts[cpu.PC]++;
            }
        }
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
