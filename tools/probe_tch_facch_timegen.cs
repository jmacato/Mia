#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

const long firstCycle = 418_300_000;
const long lastCycle = 418_700_000;
const long instructionLimit = 419_000_000;
var decoderStatusA = Convert.ToByte(
    Environment.GetEnvironmentVariable("MIA_PROBE_DECODER_STATUS_A") ?? "00",
    16);
var decoderStatusB = Convert.ToByte(
    Environment.GetEnvironmentVariable("MIA_PROBE_DECODER_STATUS_B") ?? "00",
    16);
var decodeOnTrafficAction =
    Environment.GetEnvironmentVariable("MIA_PROBE_ACTION20_DECODE") == "1";
var forceFacchStealing =
    Environment.GetEnvironmentVariable("MIA_PROBE_FACCH_STEALING") == "1";

MiaMachine? machine = null;
GsmCompatibilityCell? cell = null;
cell = new GsmCompatibilityCell(
    -49,
    0x7f80,
    randomAccessReferenceSource: () => machine is null
        ? null
        : MiaGsmRandomAccessAdapter.ReadPendingReference(machine.Cpu));
cell.QueueIncomingCall("+5551234");

machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: 30_000_000,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
    rfSignalSource: new AsicSingleChannelRfSource(-49, 0x7f80),
    fchSource: cell,
    channelDecoderSource: cell,
    channelEncoderSink: cell);
using var machineLifetime = machine;
var executionProbe = new FacchExecutionProbe(firstCycle);
machine.Clock.ScheduleAt(
    () => machine.ExecutionObserver = executionProbe,
    firstCycle - 10_000_000);

machine.Clock.ScheduleAt(
    () =>
    {
        machine.Keypad.Press(0x0f, 0x08);
        machine.InterruptController.RaiseHighPriority(
            AsicInterruptController.KeypadSource);
    },
    430_000_000);
machine.Clock.ScheduleAt(
    () => machine.Keypad.Release(0x0f, 0x08),
    434_000_000);

var trace = new List<string>();
bool InWindow() => machine.Cycles is >= firstCycle and <= lastCycle;
string State() =>
    $"enc={machine.Cpu.Data[AsicPhCommandController.EncoderContextAddress]:x2} " +
    $"dec={machine.Cpu.Data[AsicChannelDecoder.FirmwareStateAddress]:x2}";
string HexData(int address, int length) =>
    Convert.ToHexString(machine.Cpu.Data.AsSpan(address, length));

machine.TimeGenerator.ActionProgrammed += (selector, program) =>
{
    if (InWindow())
    {
        trace.Add(
            $"PROGRAM c={machine.Cycles} pc={machine.Cpu.PC:x6} {State()} " +
            $"selector={selector:x2} bytes={Convert.ToHexString(program.Bytes.Span)}");
    }
};
machine.TimeGenerator.ActionDefinitionProgrammed += (selector, definition) =>
{
    if (InWindow())
    {
        trace.Add(
            $"DEFINITION c={machine.Cycles} pc={machine.Cpu.PC:x6} {State()} " +
            $"selector={selector:x2} bytes={Convert.ToHexString(definition.Bytes.Span)}");
    }
};
machine.TimeGenerator.DescriptorProgrammed += (port, descriptor) =>
{
    if (InWindow())
    {
        trace.Add(
            $"DESCRIPTOR c={machine.Cycles} pc={machine.Cpu.PC:x6} {State()} " +
            $"port={port:x4} bytes={descriptor.Byte0:x2}{descriptor.Byte1:x2}{descriptor.Byte2:x2}");
    }
};
machine.TimeGenerator.ActionExecuted += action =>
{
    if (decodeOnTrafficAction &&
        action.ActionId == 0x20 &&
        machine.Cpu.Data[AsicChannelDecoder.FirmwareStateAddress] ==
            AsicChannelDecoder.TrafficFacchFirmwareState)
    {
        // Temporary cadence probe for the unrecovered traffic receive action.
        machine.Cpu.WriteData(
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.ControlChannelArmedControl);
        machine.Cpu.WriteData(
            AsicChannelDecoder.CommandAddress,
            AsicChannelDecoder.SchCommand);
        machine.Cpu.WriteData(
            AsicChannelDecoder.ControlAddress,
            AsicChannelDecoder.ControlChannelStartControl);
    }
    if (action.ActionId == AsicTimeGenerator.ScheduledDecoderActionId)
    {
        // Temporary status probe: source-derived payload still comes through
        // the decoder device; these two bytes explore the unrecovered traffic
        // decoder ancillary-status contract.
        machine.Cpu.Data[0x094a] = decoderStatusA;
        machine.Cpu.Data[0x094b] = decoderStatusB;
        if (forceFacchStealing)
        {
            // Temporary diagnostic only: this is the firmware-computed
            // traffic stealing/FACCH predicate, not a production state write.
            machine.Cpu.Data[0x02a6ce] = 1;
        }
    }
    if (InWindow())
    {
        trace.Add(
            $"EXECUTE c={machine.Cycles} pc={machine.Cpu.PC:x6} {State()} " +
            $"selector={action.ProgramSelector:x2} port={action.SchedulePortAddress:x4} " +
            $"action={action.ActionId:x2} operand={action.Operand:x4} " +
            $"qb={action.QuarterBit} occurrence={action.OccurrenceIndex + 1}/" +
            $"{action.OccurrenceCount} " +
            $"decoder-context={HexData(0x02a6a0, 0x30)} " +
            $"traffic-context={HexData(0x03256d, 0x78)} " +
            $"traffic-dsp={HexData(0x10071b, 0x05)} " +
            $"equalizer-mmio={HexData(0x0920, 0x0a)} " +
            $"decoder-mmio={HexData(0x0940, 0x10)}");
    }
};

while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
    $"assignment={cell.MobileTerminatedTrafficAssignmentCount}/" +
    $"{cell.MobileTerminatedTrafficAssignmentDeliveredCount}/" +
    $"{cell.MobileTerminatedTrafficAssignmentCompleteCount} " +
    $"sabm={cell.SabmCount} ua={cell.UaCount}");
foreach (var selector in new byte[] { 0x26, 0x64, 0x65, 0x66, 0x67 })
{
    Console.WriteLine(
        $"FINAL DEFINITION selector={selector:x2} bytes=" +
        (machine.TimeGenerator.ActionDefinitions.TryGetValue(
            selector,
            out var definition)
                ? Convert.ToHexString(definition.Bytes.Span)
                : "<missing>"));
}
foreach (var line in trace)
{
    Console.WriteLine(line);
}
executionProbe.Print();

sealed class FacchExecutionProbe(long firstCycle) : IMiaExecutionObserver
{
    static readonly int[] TargetPcs =
    [
        0x1aed81,
        0x1aeed9, 0x1aef7f,
        0x1aefe3, 0x1af044, 0x1af255, 0x1af321,
        0x1af459, 0x1af545, 0x1afa0a, 0x1afb13,
        0x1afd0c, 0x1afef1,
        0x1b48b1, 0x1b4aff, 0x1b502e,
        0x1af57f, 0x1af5df,
        0x1af805, 0x1af906,
        0x1afc24,
        0x1b00ee,
        0x1ace17, 0x1ace23, 0x1ace3f,
        0x1b649c, 0x1b64a8, 0x1b64db,
        0x1b4936, 0x1b4e9c,
        0x1cfb13, 0x1cfb26, 0x1cfb64,
        0x1cfcb6, 0x1cfdc2, 0x1cfe07, 0x1cfefb,
        0x1d01f2, 0x1d03f7, 0x1d05cf,
        0x163d30, 0x163d3e,
        0x1c53ef, 0x1c5423,
    ];

    readonly Dictionary<int, long> _counts =
        TargetPcs.ToDictionary(address => address, _ => 0L);
    readonly Dictionary<int, long> _firstCycles = [];
    readonly List<string> _details = [];
    readonly List<string> _signals = [];
    byte _lastDecoderState;
    byte _lastEncoderState;
    byte _lastEqualizerState;
    int _previousPc;

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        var cycle = machine.Cycles;
        if (cycle < firstCycle)
        {
            return null;
        }

        var decoderState = cpu.Data[AsicChannelDecoder.FirmwareStateAddress];
        var encoderState = cpu.Data[AsicPhCommandController.EncoderContextAddress];
        var equalizerState = cpu.Data[0x02a55e];
        if (cpu.PC == 0x001838 && _signals.Count < 512)
        {
            var arguments = cpu.Data[16] |
                cpu.Data[17] << 8 |
                cpu.Data[18] << 16;
            var physicalArguments = cpu.TranslateDataAddress(arguments);
            if (physicalArguments >= 0 &&
                physicalArguments + 1 < cpu.Data.Length)
            {
                var record = cpu.ReadData(physicalArguments) |
                    cpu.ReadData(physicalArguments + 1) << 8;
                if (record >= 0 && record + 1 < cpu.Data.Length)
                {
                    var signal = cpu.ReadData(record) |
                        cpu.ReadData(record + 1) << 8;
                    _signals.Add(
                        $"SIGNAL c={cycle} site={_previousPc:x6} " +
                        $"source={cpu.ReadData(0xf606):x2} " +
                        $"destination={cpu.Data[20]:x2} " +
                        $"signal={signal:x4} record={record:x6}");
                }
            }
        }
        if (decoderState != _lastDecoderState ||
            encoderState != _lastEncoderState ||
            equalizerState != _lastEqualizerState)
        {
            if (_details.Count < 160)
            {
                _details.Add(
                    $"STATE c={cycle} pc={cpu.PC:x6} " +
                    $"dec={_lastDecoderState:x2}->{decoderState:x2} " +
                    $"enc={_lastEncoderState:x2}->{encoderState:x2} " +
                    $"eq={_lastEqualizerState:x2}->{equalizerState:x2} " +
                    $"flags={Convert.ToHexString(cpu.Data.AsSpan(0x02a6c5, 0x0c))}");
            }
            _lastDecoderState = decoderState;
            _lastEncoderState = encoderState;
            _lastEqualizerState = equalizerState;
        }

        if (!_counts.TryGetValue(cpu.PC, out var count))
        {
            _previousPc = cpu.PC;
            return null;
        }

        _counts[cpu.PC] = count + 1;
        _firstCycles.TryAdd(cpu.PC, cycle);
        if (_details.Count < 160)
        {
            _details.Add(
                $"PC c={cycle} pc={cpu.PC:x6} prev={_previousPc:x6} " +
                $"sp={cpu.SP:x4} stack=" +
                $"{Convert.ToHexString(cpu.Data.AsSpan(cpu.SP + 1, 4))} " +
                $"count={count + 1} " +
                $"r16..23={Convert.ToHexString(cpu.Data.AsSpan(16, 8))} " +
                $"ramp={Convert.ToHexString(cpu.Data.AsSpan(0x58, 5))} " +
                $"dec={decoderState:x2} enc={encoderState:x2} eq={equalizerState:x2} " +
                $"flags={Convert.ToHexString(cpu.Data.AsSpan(0x02a6c5, 0x0c))} " +
                $"ph-message={ReadPhMessage(cpu)} " +
                $"control-out={Convert.ToHexString(cpu.Data.AsSpan(0x1006c8, 0x17))} " +
                $"facch-out={Convert.ToHexString(cpu.Data.AsSpan(0x1006df, 0x17))}");
        }
        _previousPc = cpu.PC;
        return null;
    }

    static string ReadPhMessage(AvrCore.Cpu cpu)
    {
        const int pointerAddress = 0x028369;
        var pointer = cpu.Data[pointerAddress] |
            cpu.Data[pointerAddress + 1] << 8 |
            cpu.Data[pointerAddress + 2] << 16;
        return pointer is >= 0 and < 0x1000000 - 2
            ? $"{pointer:x6}:" +
                Convert.ToHexString(cpu.Data.AsSpan(pointer, 2))
            : $"{pointer:x6}:----";
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }

    public void Print()
    {
        Console.WriteLine("EXECUTION PROBE COUNTS");
        foreach (var (pc, count) in _counts)
        {
            Console.WriteLine(
                $"  pc={pc:x6} count={count} first=" +
                (_firstCycles.TryGetValue(pc, out var cycle)
                    ? cycle
                    : "never"));
        }
        foreach (var detail in _details)
        {
            Console.WriteLine(detail);
        }
        foreach (var signal in _signals)
        {
            Console.WriteLine(signal);
        }
    }
}
