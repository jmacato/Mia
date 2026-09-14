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

var observer = new UiPathObserver();
machine.ExecutionObserver = observer;
while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
}

foreach (var line in observer.Trace)
{
    Console.WriteLine(line);
}
Console.WriteLine(
    $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
    $"trace={observer.Trace.Count} stopped={machine.IsStopped} " +
    $"reason={machine.StopReason}");

sealed class UiPathObserver : IMiaExecutionObserver
{
    const byte UiProcess = 0x63;
    bool _active;
    ushort _lastSignal;
    ushort _lastSubtype;

    public List<string> Trace { get; } = [];

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.ReadData(0xf606) != UiProcess)
        {
            return null;
        }

        if (cpu.PC == 0x001912 && TryReadReceive(cpu, out var signal, out var subtype))
        {
            _lastSignal = signal;
            _lastSubtype = subtype;
            if (signal == 0x171c)
            {
                _active = true;
            }
        }

        if (!_active)
        {
            return null;
        }

        var logicalZ = cpu.Data[30] | cpu.Data[31] << 8 | cpu.Data[0x5b] << 16;
        Trace.Add(
            $"{Trace.Count}\t{machine.ExecutedInstructions}\t{machine.Cycles}\t" +
            $"{cpu.PC:x6}\t{cpu.SP:x4}\t" +
            $"{Convert.ToHexString(cpu.Data.AsSpan(16, 10))}\t" +
            $"{cpu.Data[0x5a]:x2}{cpu.Data[29]:x2}{cpu.Data[28]:x2}\t" +
            $"{logicalZ:x6}\t{cpu.SREG:x2}");

        if (Trace.Count >= 200_000)
        {
            return "UI 0x171c trace limit";
        }

        if (cpu.PC == 0x00191d && _lastSignal == 0x1a35 && _lastSubtype == 0x1602)
        {
            return "UI completed 0x1a35 subtype 0x1602";
        }

        return null;
    }

    static bool TryReadReceive(AvrCore.Cpu cpu, out ushort signal, out ushort subtype)
    {
        var payload = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        if ((uint)payload >= (uint)(cpu.Data.Length - 4))
        {
            signal = 0;
            subtype = 0;
            return false;
        }

        signal = (ushort)(cpu.Data[payload] | cpu.Data[payload + 1] << 8);
        subtype = (ushort)(cpu.Data[payload + 2] | cpu.Data[payload + 3] << 8);
        return true;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
