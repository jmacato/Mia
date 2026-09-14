#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using System.Security.Cryptography;
using Mia.Emulator;

var instructionLimit = args.Length > 0 && long.TryParse(args[0], out var parsedLimit)
    ? parsedLimit
    : 350_000_000;
var gdfsPath = args.Length > 1
    ? args[1]
    : "images/T68i_Full_GDFS.compact.raw";
var schedulingMode = args.Length > 2 && args[2] == "cycle-locked"
    ? MiaCoreSchedulingMode.CycleLocked
    : MiaCoreSchedulingMode.CoarseParallel;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes(gdfsPath),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: schedulingMode);

machine.ExecutionObserver = new UiReceiveObserver();
var frameVersion = 0;
while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
    if (machine.FrameVersion == frameVersion)
    {
        continue;
    }

    frameVersion = machine.FrameVersion;
    Console.WriteLine(
        $"FRAME version={frameVersion} i={machine.ExecutedInstructions} " +
        $"c={machine.Cycles} hash=" +
        Convert.ToHexStringLower(SHA256.HashData(machine.Frame.Span)));
}

Console.WriteLine(
    $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
    $"frames={machine.FrameVersion} stopped={machine.IsStopped} " +
    $"reason={machine.StopReason}");

sealed class UiReceiveObserver : IMiaExecutionObserver
{
    // At this return site, the native receive core has removed the six-byte
    // queue header and returned the payload pointer in r18:r17:r16.
    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.PC != 0x001912 || cpu.ReadData(0xf606) != 0x63)
        {
            return null;
        }

        var payload = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        if ((uint)payload >= (uint)(cpu.Data.Length - 2))
        {
            Console.WriteLine(
                $"UI-RECEIVE i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"invalid-payload={payload:x6}");
            return null;
        }

        var count = Math.Min(24, cpu.Data.Length - payload);
        var signal = cpu.Data[payload] | cpu.Data[payload + 1] << 8;
        Console.WriteLine(
            $"UI-RECEIVE i={machine.ExecutedInstructions} c={machine.Cycles} " +
            $"payload={payload:x6} signal={signal:x4} " +
            $"bytes={Convert.ToHexString(cpu.Data.AsSpan(payload, count))}");
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
