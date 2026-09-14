#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using Mia.Emulator;

if (args.Length is < 1 or > 3 ||
    !long.TryParse(args[0], out var instructionLimit) ||
    instructionLimit <= 0)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/probe_secondary_ports.cs -- " +
        "INSTRUCTIONS [IDENTITY_HEX] [STATUS_HEX]");
    return 2;
}

var identity = args.Length >= 2 ? Convert.ToByte(args[1], 16) : (byte)0x41;
var status = args.Length >= 3 ? Convert.ToByte(args[2], 16) : (byte)0x00;
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
    liveGsm: new MiaLiveGsmOptions(),
    secondaryPortProfile: new(identity, status));

var observer = new SecondaryPortObserver();
machine.ExecutionObserver = observer;
var outputChanges = new List<OutputChange>();
machine.SecondaryPorts.OutputStateChanged += state =>
    outputChanges.Add(new(
        machine.Cycles,
        machine.ExecutedInstructions,
        machine.Cpu.PC,
        machine.Cpu.ReadData(0xf606),
        state));
var opaqueWrites = new Dictionary<(byte Register, byte Value), long>();
machine.SecondaryPorts.OpaqueRegisterWritten += (register, value) =>
    opaqueWrites[(register, value)] =
        opaqueWrites.GetValueOrDefault((register, value)) + 1;

while (machine.ExecutedInstructions < instructionLimit && !machine.IsStopped)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"profile identity=0x{identity:x2} status=0x{status:x2}; " +
    $"instructions={machine.ExecutedInstructions:n0} cycles={machine.Cycles:n0}; " +
    $"stopped={machine.IsStopped} {machine.StopReason}");
var liveStatus = machine.LiveGsm?.GetStatus(machine);
Console.WriteLine(
    $"live-gsm registered={liveStatus?.Registered} " +
    $"rssi={liveStatus?.RssiLevel}; " +
    $"reads={machine.SecondaryPorts.ReadCount:n0} " +
    $"writes={machine.SecondaryPorts.WriteCount:n0} " +
    $"opaque={machine.SecondaryPorts.OpaqueWriteCount:n0} " +
    $"unsupported-reads={machine.SecondaryPorts.UnsupportedReadCount:n0} " +
    $"rejected-writes={machine.SecondaryPorts.RejectedWriteCount:n0}; " +
    $"final={Format(machine.SecondaryPorts.OutputState)}");

Console.WriteLine("dispatcher command/producer counts:");
foreach (var pair in observer.Counts
             .OrderBy(pair => pair.Key.Command)
             .ThenBy(pair => pair.Key.Process)
             .ThenBy(pair => pair.Key.ReturnPc))
{
    Console.WriteLine(
        $"  command={pair.Key.Command,2} process=0x{pair.Key.Process:x2} " +
        $"return=0x{pair.Key.ReturnPc:x6} count={pair.Value:n0}");
}

Console.WriteLine("effective dispatcher transitions:");
foreach (var transition in observer.Transitions)
{
    Console.WriteLine(
        $"  cycle={transition.Cycle,10} i={transition.Instruction,10} " +
        $"process=0x{transition.Process:x2} " +
        $"signal=0x{transition.Signal.Signal:x4} " +
        $"source=0x{transition.Signal.Source:x2} " +
        $"command={transition.Command,2} " +
        $"return=0x{transition.ReturnPc:x6} " +
        $"0x{transition.OldValue:x2}->0x{transition.NewValue:x2} " +
        $"signal-cycle={transition.Signal.Cycle} " +
        $"stack={transition.Stack}");
}

Console.WriteLine("secondary-port output transactions:");
foreach (var change in outputChanges)
{
    Console.WriteLine(
        $"  cycle={change.Cycle,10} i={change.Instruction,10} " +
        $"pc=0x{change.Pc:x6} process=0x{change.Process:x2} " +
        Format(change.State));
}

Console.WriteLine("opaque write histogram:");
foreach (var pair in opaqueWrites.OrderBy(pair => pair.Key.Register))
{
    Console.WriteLine(
        $"  register=0x{pair.Key.Register:x2} " +
        $"value=0x{pair.Key.Value:x2} count={pair.Value:n0}");
}

return 0;

static string Format(MiaSecondaryPortOutputState state) =>
    $"40=0x{state.Register40:x2} 48=0x{state.Register48:x2} " +
    $"80=0x{state.Register80:x2}";

readonly record struct OutputChange(
    long Cycle,
    long Instruction,
    int Pc,
    byte Process,
    MiaSecondaryPortOutputState State);

readonly record struct SignalContext(
    long Cycle,
    byte Source,
    ushort Signal)
{
    public static SignalContext None => new(-1, 0xff, 0xffff);
}

readonly record struct DispatchKey(
    byte Command,
    byte Process,
    int ReturnPc);

readonly record struct DispatchTransition(
    long Cycle,
    long Instruction,
    byte Process,
    SignalContext Signal,
    byte Command,
    int ReturnPc,
    byte OldValue,
    byte NewValue,
    string Stack);

sealed class SecondaryPortObserver : IMiaExecutionObserver
{
    const int DispatcherEntry = 0x0761c9;
    const int DispatcherExit = 0x07643d;
    const int ReceiveEntry = 0x001912;
    const int Shadow40Address = 0x028477;
    readonly Dictionary<byte, SignalContext> _lastSignals = [];
    ActiveDispatch? _active;

    public Dictionary<DispatchKey, long> Counts { get; } = [];

    public List<DispatchTransition> Transitions { get; } = [];

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.PC == ReceiveEntry)
        {
            ObserveReceive(machine);
        }
        if (cpu.PC == DispatcherEntry)
        {
            var process = cpu.ReadData(0xf606);
            var returnPc = ReadReturnPc(cpu);
            var key = new DispatchKey(cpu.Data[16], process, returnPc);
            Counts[key] = Counts.GetValueOrDefault(key) + 1;
            _active = new(
                machine.Cycles,
                machine.ExecutedInstructions,
                process,
                _lastSignals.GetValueOrDefault(process, SignalContext.None),
                cpu.Data[16],
                returnPc,
                cpu.ReadData(Shadow40Address),
                Convert.ToHexString(cpu.Data.AsSpan(
                    cpu.SP + 1,
                    Math.Min(48, cpu.Data.Length - cpu.SP - 1))));
        }
        else if (cpu.PC == DispatcherExit && _active is { } active)
        {
            var newValue = cpu.ReadData(Shadow40Address);
            if (active.OldValue != newValue)
            {
                Transitions.Add(new(
                    active.Cycle,
                    active.Instruction,
                    active.Process,
                    active.Signal,
                    active.Command,
                    active.ReturnPc,
                    active.OldValue,
                    newValue,
                    active.Stack));
            }
            _active = null;
        }
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }

    void ObserveReceive(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        var logicalPayload = cpu.Data[16] |
            cpu.Data[17] << 8 |
            cpu.Data[18] << 16;
        if ((logicalPayload & 0xffff) == 0xfdfd)
        {
            return;
        }
        var payload = cpu.TranslateDataAddress(logicalPayload);
        if ((uint)payload > (uint)(cpu.Data.Length - 2))
        {
            return;
        }
        _lastSignals[cpu.ReadData(0xf606)] = new(
            machine.Cycles,
            payload > 0 ? cpu.ReadData(payload - 1) : (byte)0xff,
            (ushort)(cpu.ReadData(payload) | cpu.ReadData(payload + 1) << 8));
    }

    static int ReadReturnPc(AvrCore.Cpu cpu)
    {
        var stack = cpu.SP;
        return cpu.ReadData(stack + 1) << 16 |
            cpu.ReadData(stack + 2) << 8 |
            cpu.ReadData(stack + 3);
    }

    readonly record struct ActiveDispatch(
        long Cycle,
        long Instruction,
        byte Process,
        SignalContext Signal,
        byte Command,
        int ReturnPc,
        byte OldValue,
        string Stack);
}
