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

machine.ExecutionObserver = new SimStatusObserver();
while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason}");

sealed class SimStatusObserver : IMiaExecutionObserver
{
    static readonly HashSet<int> Checkpoints =
    [
        0x01e104,
        0x01e10f,
        0x01e110,
        0x01e13a,
        0x01e140,
        0x01e16d,
        0x01e170,
        0x01e17a,
        0x01e17e,
    ];

    int _invocation;
    int _previousPc;

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        var previousPc = _previousPc;
        _previousPc = cpu.PC;
        if (cpu.ReadData(0xf606) != 0x42 || !Checkpoints.Contains(cpu.PC))
        {
            return null;
        }

        if (cpu.PC == 0x01e104)
        {
            _invocation++;
        }

        var logicalZ = cpu.Data[30] | cpu.Data[31] << 8 | cpu.Data[0x5b] << 16;
        var physicalZ = cpu.TranslateDataAddress(logicalZ);
        var zBytes = (uint)physicalZ < (uint)cpu.Data.Length
            ? Convert.ToHexString(cpu.Data.AsSpan(
                physicalZ,
                Math.Min(16, cpu.Data.Length - physicalZ)))
            : "invalid";
        var suffix = string.Empty;
        if (cpu.PC == 0x01e17e)
        {
            var logicalArgument = cpu.Data[16] |
                cpu.Data[17] << 8 |
                cpu.Data[18] << 16;
            var physicalArgument = cpu.TranslateDataAddress(logicalArgument);
            if ((uint)physicalArgument < (uint)(cpu.Data.Length - 1))
            {
                var payload = cpu.Data[physicalArgument] |
                    cpu.Data[physicalArgument + 1] << 8;
                if ((uint)payload < (uint)(cpu.Data.Length - 2))
                {
                    var count = Math.Min(24, cpu.Data.Length - payload);
                    suffix = $" destination={cpu.Data[20]:x2} payload={payload:x4} " +
                        $"signal={(cpu.Data[payload] | cpu.Data[payload + 1] << 8):x4} " +
                        $"payload-bytes={Convert.ToHexString(cpu.Data.AsSpan(payload, count))}";
                }
            }
        }

        Console.WriteLine(
            $"SIM-STATUS invocation={_invocation} i={machine.ExecutedInstructions} " +
            $"c={machine.Cycles} pc={cpu.PC:x6} from={previousPc:x6} " +
            $"regs16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
            $"y={cpu.Data[0x5a]:x2}{cpu.Data[29]:x2}{cpu.Data[28]:x2} " +
            $"z={logicalZ:x6}->{physicalZ:x6}:{zBytes}{suffix}");
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
