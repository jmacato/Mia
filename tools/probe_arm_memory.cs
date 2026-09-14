#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

if (args.Length < 2 ||
    !uint.TryParse(args[0], System.Globalization.NumberStyles.HexNumber, null, out var address) ||
    !int.TryParse(args[1], out var length))
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/probe_arm_memory.cs -- HEX_ADDRESS LENGTH [INSTRUCTIONS]");
    return 2;
}

var instructionLimit = args.Length > 2 && long.TryParse(args[2], out var parsedLimit)
    ? parsedLimit
    : 20_000_000;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems((int)Math.Min(
        262_144,
        instructionLimit - machine.ExecutedInstructions));
}

Console.WriteLine(
    $"instructions={machine.ExecutedInstructions:n0} " +
    $"modem-instructions={machine.Modem?.Instructions:n0} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason}");
Console.WriteLine(
    $"0x{address:x8}: " +
    Convert.ToHexString(machine.Modem!.Bus.SnapshotExternal(address, length)));
return 0;
