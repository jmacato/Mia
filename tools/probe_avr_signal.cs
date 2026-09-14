#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

if (args.Length < 1 ||
    !ushort.TryParse(
        args[0],
        System.Globalization.NumberStyles.HexNumber,
        null,
        out ushort tracedSignal))
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/probe_avr_signal.cs -- " +
        "HEX_SIGNAL [INSTRUCTIONS] [AVR_ADDRESS LENGTH] [SWBP_ENDPOINT]");
    return 2;
}

long instructionLimit =
    args.Length > 1 && long.TryParse(args[1], out long parsedLimit)
        ? parsedLimit
        : 100_000_000;

byte? watchedSwbpEndpoint =
    args.Length >= 5 &&
    byte.TryParse(
        args[4],
        System.Globalization.NumberStyles.HexNumber,
        null,
        out byte parsedEndpoint)
        ? parsedEndpoint
        : null;
var observer = new SignalReceiveObserver(tracedSignal, watchedSwbpEndpoint);
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
machine.ExecutionObserver = observer;

while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems((int)Math.Min(
        262_144,
        instructionLimit - machine.ExecutedInstructions));
}

Console.WriteLine(
    $"instructions={machine.ExecutedInstructions:n0} " +
    $"modem-instructions={machine.Modem?.Instructions:n0} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason} " +
    $"matches={observer.MatchCount:n0}");
foreach (string line in observer.Trace)
{
    Console.WriteLine(line);
}
if (args.Length >= 4 &&
    int.TryParse(
        args[2],
        System.Globalization.NumberStyles.HexNumber,
        null,
        out int dumpAddress) &&
    int.TryParse(args[3], out int dumpLength))
{
    byte[] dump = Enumerable.Range(0, dumpLength)
        .Select(offset => machine.Cpu.ReadData(dumpAddress + offset))
        .ToArray();
    Console.WriteLine(
        $"avr-logical-0x{dumpAddress:x6}: " +
        Convert.ToHexString(dump));
}

return 0;

sealed class SignalReceiveObserver(
    ushort tracedSignal,
    byte? watchedSwbpEndpoint) : IMiaExecutionObserver
{
    const int ReceiveReturnPc = 0x001912;
    const int CurrentProcessIdAddress = 0x00f606;
    const int SwbpEndpointTableAddress = 0x02df0e;
    const int MaximumTraceEntries = 1_000;
    const int PostReceiveInstructionCount = 64;

    int _remainingPostReceiveInstructions;
    uint? _previousSwbpEntry;

    public int MatchCount { get; private set; }

    public List<string> Trace { get; } = [];

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (watchedSwbpEndpoint is { } endpoint)
        {
            int address = SwbpEndpointTableAddress + endpoint * sizeof(uint);
            uint entry =
                (uint)cpu.ReadData(address) |
                (uint)cpu.ReadData(address + 1) << 8 |
                (uint)cpu.ReadData(address + 2) << 16 |
                (uint)cpu.ReadData(address + 3) << 24;
            if (_previousSwbpEntry != entry &&
                Trace.Count < MaximumTraceEntries)
            {
                Trace.Add(
                    $"swbp-entry i={machine.ExecutedInstructions:n0} " +
                    $"pid=0x{cpu.ReadData(CurrentProcessIdAddress):x2} " +
                    $"pc=0x{cpu.PC:x6} endpoint=0x{endpoint:x2} " +
                    $"entry=0x{entry:x8} previous=" +
                    (_previousSwbpEntry is { } previous
                        ? $"0x{previous:x8}"
                        : "unset"));
                _previousSwbpEntry = entry;
            }
        }
        if (_remainingPostReceiveInstructions-- > 0 &&
            Trace.Count < MaximumTraceEntries)
        {
            Trace.Add(
                $"post-receive i={machine.ExecutedInstructions:n0} " +
                $"pid=0x{cpu.ReadData(CurrentProcessIdAddress):x2} " +
                $"pc=0x{cpu.PC:x6} " +
                $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))}");
        }
        if (cpu.PC != ReceiveReturnPc)
        {
            return null;
        }

        int logicalPayload =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        int payload = cpu.TranslateDataAddress(logicalPayload);
        if ((uint)payload > (uint)(cpu.Data.Length - sizeof(ushort)) ||
            BitConverter.ToUInt16(cpu.Data, payload) != tracedSignal)
        {
            return null;
        }

        MatchCount++;
        if (Trace.Count < MaximumTraceEntries)
        {
            int length = Math.Min(128, cpu.Data.Length - payload);
            Trace.Add(
                $"i={machine.ExecutedInstructions:n0} " +
                $"pid=0x{cpu.ReadData(CurrentProcessIdAddress):x2} " +
                $"logical=0x{logicalPayload:x6} physical=0x{payload:x6} " +
                $"bytes={Convert.ToHexString(cpu.Data.AsSpan(payload, length))}");
            _remainingPostReceiveInstructions = PostReceiveInstructionCount;
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
