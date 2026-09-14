#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

var instructionLimit = args.Length > 0 && long.TryParse(args[0], out var parsedLimit)
    ? parsedLimit
    : 100_000_000;
var gdfsPath = args.Length > 1
    ? args[1]
    : "images/T68i_Full_GDFS.compact.raw";

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes(gdfsPath),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

machine.ExecutionObserver = new SimStatusIntervalObserver();
while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason}");

sealed class SimStatusIntervalObserver : IMiaExecutionObserver
{
    byte[]? _snapshot;
    long _receiveCycle;
    int _previousPc;

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        var previousPc = _previousPc;
        _previousPc = cpu.PC;
        if (cpu.ReadData(0xf606) != 0x42)
        {
            return null;
        }

        if (cpu.PC == 0x001912)
        {
            var payload = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
            if ((uint)payload < (uint)(cpu.Data.Length - 24) &&
                (cpu.Data[payload] | cpu.Data[payload + 1] << 8) == 0x0514)
            {
                _snapshot = cpu.Data.ToArray();
                _receiveCycle = machine.Cycles;
                Console.WriteLine(
                    $"BEGIN i={machine.ExecutedInstructions} c={machine.Cycles} " +
                    $"payload={payload:x6} bytes=" +
                    Convert.ToHexString(cpu.Data.AsSpan(payload, 24)));
            }
            return null;
        }

        if (_snapshot is null || cpu.PC != 0x01e104)
        {
            return null;
        }

        // Only the final SIM-status path enters from 0x01ddbf.
        if (previousPc != 0x01ddbf || cpu.Data[21] != 0xff || cpu.Data[22] != 0xb5)
        {
            return null;
        }

        Console.WriteLine(
            $"END-INTERVAL i={machine.ExecutedInstructions} c={machine.Cycles} " +
            $"delta-cycles={machine.Cycles - _receiveCycle}");
        PrintChangedRanges(_snapshot, cpu.Data);
        PrintOccurrences(cpu.Data, [0x00, 0x10, 0x00, 0x80]);
        PrintOccurrences(cpu.Data, [0x00, 0x00, 0x00, 0x00]);
        return "captured final SIM-status handler interval";
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }

    static void PrintChangedRanges(byte[] before, byte[] after)
    {
        for (var index = 0; index < before.Length; index++)
        {
            if (before[index] == after[index])
            {
                continue;
            }

            var start = index;
            var lastChanged = index;
            for (index++; index < before.Length; index++)
            {
                if (before[index] != after[index])
                {
                    lastChanged = index;
                }
                if (index - lastChanged > 8)
                {
                    break;
                }
            }

            var end = Math.Min(before.Length, lastChanged + 1);
            var contextStart = Math.Max(0, start - 8);
            var contextEnd = Math.Min(before.Length, end + 8);
            Console.WriteLine(
                $"CHANGE {start:x6}-{end - 1:x6} " +
                $"before={Convert.ToHexString(before.AsSpan(contextStart, contextEnd - contextStart))} " +
                $"after={Convert.ToHexString(after.AsSpan(contextStart, contextEnd - contextStart))}");
            index = lastChanged;
        }
    }

    static void PrintOccurrences(byte[] data, byte[] pattern)
    {
        var found = 0;
        for (var index = 0; index <= data.Length - pattern.Length; index++)
        {
            if (!data.AsSpan(index, pattern.Length).SequenceEqual(pattern))
            {
                continue;
            }
            if (found++ < 32)
            {
                Console.WriteLine(
                    $"PATTERN {Convert.ToHexString(pattern)} at={index:x6} context=" +
                    Convert.ToHexString(data.AsSpan(
                        Math.Max(0, index - 8),
                        Math.Min(data.Length, index + pattern.Length + 8) -
                        Math.Max(0, index - 8))));
            }
        }
        Console.WriteLine($"PATTERN-COUNT {Convert.ToHexString(pattern)} count={found}");
    }
}
