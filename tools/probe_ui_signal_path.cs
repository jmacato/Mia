#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

var targetSignal = args.Length > 0
    ? Convert.ToUInt16(args[0], 16)
    : (ushort)0x163a;
var traceLimit = args.Length > 1 && int.TryParse(args[1], out var parsedTraceLimit)
    ? parsedTraceLimit
    : 5_000;
var instructionLimit = args.Length > 2 && long.TryParse(args[2], out var parsedLimit)
    ? parsedLimit
    : 100_000_000;
var compact = args.Length > 3 && args[3] == "compact";
var targetOccurrence = args.Length > 4 && int.TryParse(args[4], out var parsedOccurrence)
    ? parsedOccurrence
    : 1;

MiaMachine? machine = null;
var cell = new GsmCompatibilityCell(
    -49,
    0x0800,
    randomAccessReferenceSource: () => machine is null
        ? null
        : MiaGsmRandomAccessAdapter.ReadPendingReference(machine.Cpu));

using (machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
    rfSignalSource: new AsicSingleChannelRfSource(-49, 0x0800),
    fchSource: cell,
    equalizerSource: cell,
    channelDecoderSource: cell,
    channelEncoderSink: cell))
{
    var observer = new UiSignalPathObserver(
        targetSignal,
        traceLimit,
        compact,
        targetOccurrence);
    machine.ExecutionObserver = observer;
    machine.SetPowerKey(pressed: true);
    var powerReleased = false;

    while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
    {
        machine.RunWorkItems(262_144);
        if (!powerReleased && machine.Cycles >= 30_000_000)
        {
            machine.SetPowerKey(pressed: false);
            powerReleased = true;
        }
    }

    Console.WriteLine(
        $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
        $"registered={cell.Registered} target-seen={observer.TargetSeen} " +
        $"stopped={machine.IsStopped} reason={machine.StopReason}");
}

sealed class UiSignalPathObserver(
    ushort targetSignal,
    int traceLimit,
    bool compact,
    int targetOccurrence) :
    IMiaExecutionObserver
{
    int _remaining;
    int _previousPc = -1;
    int _previousOpcode;
    byte _modelLevel;
    byte _widgetLevel;
    int _targetSeenCount;

    public bool TargetSeen { get; private set; }

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.PC == 0x001912 && cpu.ReadData(0xf606) == 0x63)
        {
            var payload = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
            if ((uint)payload < (uint)(cpu.Data.Length - 2))
            {
                var signal = (ushort)(
                    cpu.Data[payload] | cpu.Data[payload + 1] << 8);
                if (!TargetSeen && signal == targetSignal &&
                    ++_targetSeenCount == targetOccurrence)
                {
                    TargetSeen = true;
                    _remaining = traceLimit;
                    _modelLevel = cpu.Data[0x05039e];
                    _widgetLevel = cpu.Data[0x050817];
                    Console.WriteLine(
                        $"TARGET i={machine.ExecutedInstructions} c={machine.Cycles} " +
                        $"payload={payload:x6} bytes=" +
                        Convert.ToHexString(cpu.Data.AsSpan(
                            payload,
                            Math.Min(32, cpu.Data.Length - payload))));
                }
                else if (TargetSeen && compact)
                {
                    Console.WriteLine(
                        $"NEXT-UI c={machine.Cycles} signal={signal:x4}");
                    return $"next UI signal 0x{signal:x4} before RSSI setter";
                }
            }
        }

        if (_remaining <= 0)
        {
            return TargetSeen ? $"completed {traceLimit} instructions" : null;
        }

        var opcode = cpu.GetProgWord(cpu.PC);
        if (!compact)
        {
            Console.WriteLine(
                $"TRACE c={machine.Cycles} pc={cpu.PC:x6} sp={cpu.SP:x4} " +
                $"op={opcode:x4} r16..31=" +
                $"{Convert.ToHexString(cpu.Data.AsSpan(16, 16))} " +
                $"RAMP={cpu.Data[0x58]:x2}{cpu.Data[0x59]:x2}" +
                $"{cpu.Data[0x5a]:x2}{cpu.Data[0x5b]:x2} " +
                $"SREG={cpu.SREG:x2}");
        }
        else if (_previousPc >= 0 &&
                 Math.Abs(cpu.PC - _previousPc) > 0x20)
        {
            Console.WriteLine(
                $"FLOW c={machine.Cycles} {_previousPc:x6}/{_previousOpcode:x4}" +
                $"->{cpu.PC:x6}/{opcode:x4} sp={cpu.SP:x4}");
        }

        var modelLevel = cpu.Data[0x05039e];
        var widgetLevel = cpu.Data[0x050817];
        if (modelLevel != _modelLevel || widgetLevel != _widgetLevel)
        {
            Console.WriteLine(
                $"LEVEL c={machine.Cycles} pc={cpu.PC:x6} " +
                $"model={_modelLevel}->{modelLevel} " +
                $"widget={_widgetLevel}->{widgetLevel}");
            _modelLevel = modelLevel;
            _widgetLevel = widgetLevel;
        }
        if (compact && cpu.PC == 0x09fc00)
        {
            Console.WriteLine(
                $"SETTER c={machine.Cycles} level={cpu.Data[20]} " +
                $"model={modelLevel} widget={widgetLevel}");
            return "reached RSSI setter";
        }

        _previousPc = cpu.PC;
        _previousOpcode = opcode;
        _remaining--;
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
