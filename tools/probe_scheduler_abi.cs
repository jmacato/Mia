#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

var limit = args.Length > 0 && long.TryParse(args[0], out var parsed)
    ? parsed
    : 50_000_000;
var firmware = File.ReadAllBytes("flat.bin");
var gdfsPath = args.Length > 1
    ? args[1]
    : "images/T68i_Full_GDFS.raw";
var gdfs = File.ReadAllBytes(gdfsPath);
var modemPath = "images/t68i_R8A015_125326_Modem.bih";
var modem = File.Exists(modemPath) ? File.ReadAllBytes(modemPath) : [];
var virtualSim = args.Contains("--virtual-sim", StringComparer.Ordinal);
using var machine = new MiaMachine(
    firmware,
    gdfs,
    modem,
    Convert.FromHexString("321A065432100654"),
    virtualSim: virtualSim,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

var primaryI2cTrace = new Queue<string>();
void RecordPrimaryI2c(string operation, int address, byte value)
{
    if (primaryI2cTrace.Count == 256)
    {
        primaryI2cTrace.Dequeue();
    }
    var x = machine.Cpu.Data[26] |
            machine.Cpu.Data[27] << 8 |
            machine.Cpu.Data[0x59] << 16;
    primaryI2cTrace.Enqueue(
        $"PRIMARY-I2C {operation} i={machine.ExecutedInstructions} c={machine.Cycles} " +
        $"pc={machine.Cpu.PC:x6} p={machine.Cpu.ReadData(0xf606):x2} " +
        $"address={address:x4} value={value:x2} " +
        $"raw={Convert.ToHexString(machine.Cpu.Data.AsSpan(0x0839, 4))} " +
        $"x={x:x6}->{machine.Cpu.TranslateDataAddress(x):x6} " +
        $"regs={Convert.ToHexString(machine.Cpu.Data.AsSpan(16, 16))} " +
        $"ext={Convert.ToHexString(machine.Cpu.Data.AsSpan(0x58, 8))}");
}
if (Environment.GetEnvironmentVariable("TRACE_PRIMARY_I2C") == "1")
{
    foreach (var address in new[] { 0x0839, 0x083a })
    {
        var previous = machine.Cpu.WriteHooks[address];
        machine.Cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
        {
            RecordPrimaryI2c("WRITE", hookAddress, value);
            return previous?.Invoke(value, oldValue, hookAddress, mask) ?? false;
        };
    }
    foreach (var address in new[] { 0x0839, 0x083b })
    {
        var previous = machine.Cpu.ReadHooks[address];
        machine.Cpu.ReadHooks[address] = hookAddress =>
        {
            var value = previous?.Invoke(hookAddress) ?? machine.Cpu.Data[hookAddress];
            RecordPrimaryI2c("READ", hookAddress, value);
            return value;
        };
    }
}

foreach (var text in (Environment.GetEnvironmentVariable("TRACE_WRITES") ?? string.Empty)
             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    if (!int.TryParse(
            text,
            System.Globalization.NumberStyles.HexNumber,
            null,
            out var address) ||
        address < 0 || address >= machine.Cpu.Data.Length)
    {
        throw new ArgumentException($"Invalid TRACE_WRITES address '{text}'.");
    }
    var previous = machine.Cpu.WriteHooks[address];
    machine.Cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
    {
        Console.WriteLine(
            $"DATA-WRITE i={machine.ExecutedInstructions} c={machine.Cycles} " +
            $"pc={machine.Cpu.PC:x6} p={machine.Cpu.ReadData(0xf606):x2} " +
            $"address={hookAddress:x6} old={oldValue:x2} value={value:x2} mask={mask:x2} " +
            $"regs={Convert.ToHexString(machine.Cpu.Data.AsSpan(0, 32))} " +
            $"ext={Convert.ToHexString(machine.Cpu.Data.AsSpan(0x58, 8))}");
        return previous?.Invoke(value, oldValue, hookAddress, mask) ?? false;
    };
}

var observer = new SchedulerObserver(Environment.GetEnvironmentVariable("BRIEF") == "1");
machine.ExecutionObserver = observer;
var traceFrameTiming = Environment.GetEnvironmentVariable("TRACE_FRAME_TIMING") == "1";
var observedFrameVersion = 0;
var interruptTransitions = new Queue<string>();
long interruptTransitionCount = 0;
var tracedIrqPc = int.TryParse(
    Environment.GetEnvironmentVariable("TRACE_IRQ_PC"),
    System.Globalization.NumberStyles.HexNumber,
    null,
    out var parsedTraceIrqPc)
    ? parsedTraceIrqPc
    : (int?)null;
var tracedIrqArmed = false;
byte[]? tracedIrqData = null;
machine.Cpu.GlobalInterruptEnableChanged += enabled =>
{
    interruptTransitionCount++;
    if (interruptTransitions.Count == 32)
    {
        interruptTransitions.Dequeue();
    }
    interruptTransitions.Enqueue(
        $"IRQ-I enabled={enabled} instruction={machine.ExecutedInstructions:n0} " +
        $"cycle={machine.Cycles:n0} pc={machine.Cpu.PC:x6} sp={machine.Cpu.SP:x4} " +
        $"process={machine.Cpu.ReadData(0xf606):x2} nesting={machine.Cpu.ReadData(0xf600):x2} " +
        $"source={machine.Cpu.ReadData(0x0816):x2}");
    if (!enabled && tracedIrqPc is { } tracePc && machine.Cpu.PC == tracePc)
    {
        tracedIrqArmed = true;
        var cpu = machine.Cpu;
        tracedIrqData = cpu.Data.ToArray();
        var first = Math.Max(0, cpu.SP - 16);
        var count = Math.Min(cpu.Data.Length - first, 96);
        Console.WriteLine(
            $"IRQ-EDGE pc={cpu.PC:x6} sp={cpu.SP:x4} " +
            $"source={cpu.ReadData(0x0816):x2} " +
            $"regs={Convert.ToHexString(cpu.Data.AsSpan(0, 32))} " +
            $"ext={Convert.ToHexString(cpu.Data.AsSpan(0x58, 8))}");
        Console.WriteLine(
            $"IRQ-EDGE-STACK {first:x6}:" +
            Convert.ToHexString(cpu.Data.AsSpan(first, count)));
        Console.WriteLine(
            "IRQ-EDGE-DB " + Convert.ToHexString(cpu.Data.AsSpan(0x2db70, 0xa0)));
    }
    else if (enabled && tracedIrqArmed && machine.Cpu.PC == tracedIrqPc)
    {
        tracedIrqArmed = false;
        var cpu = machine.Cpu;
        var first = Math.Max(0, cpu.SP - 19);
        var count = Math.Min(cpu.Data.Length - first, 96);
        Console.WriteLine(
            $"IRQ-RESUME pc={cpu.PC:x6} sp={cpu.SP:x4} " +
            $"source={cpu.ReadData(0x0816):x2} " +
            $"regs={Convert.ToHexString(cpu.Data.AsSpan(0, 32))} " +
            $"ext={Convert.ToHexString(cpu.Data.AsSpan(0x58, 8))}");
        Console.WriteLine(
            $"IRQ-RESUME-STACK {first:x6}:" +
            Convert.ToHexString(cpu.Data.AsSpan(first, count)));
        Console.WriteLine(
            "IRQ-RESUME-DB " + Convert.ToHexString(cpu.Data.AsSpan(0x2db70, 0xa0)));
        if (tracedIrqData is { } before)
        {
            var changes = new List<string>();
            var totalChanges = 0;
            for (var address = 0; address < before.Length; address++)
            {
                if (before[address] == cpu.Data[address])
                {
                    continue;
                }
                totalChanges++;
                if (changes.Count < 512)
                {
                    changes.Add($"{address:x6}:{before[address]:x2}>{cpu.Data[address]:x2}");
                }
            }
            Console.WriteLine(
                $"IRQ-RESUME-DIFF count={totalChanges} " + string.Join(' ', changes));
            tracedIrqData = null;
        }
    }
};
try
{
    while (machine.ExecutedInstructions < limit && !machine.IsStopped)
    {
        machine.RunWorkItems(int.TryParse(
            Environment.GetEnvironmentVariable("WORK_BUDGET"),
            out var workBudget)
            ? workBudget
            : 262_144);
        if (traceFrameTiming && machine.FrameVersion != observedFrameVersion)
        {
            observedFrameVersion = machine.FrameVersion;
            Console.WriteLine(
                $"LCD-FRAME version={observedFrameVersion} " +
                $"i={machine.ExecutedInstructions} c={machine.Cycles} " +
                "hash=" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(machine.Frame.Span))
                    .ToLowerInvariant());
        }
    }
}
catch
{
    foreach (var trace in primaryI2cTrace)
    {
        Console.WriteLine(trace);
    }
    Console.WriteLine(
        $"FAILURE i={machine.ExecutedInstructions} c={machine.Cycles} " +
        $"pc={machine.Cpu.PC:x6} sp={machine.Cpu.SP:x4} " +
        $"last-write-pc={machine.PrimaryI2c.LastWritePc:x6} " +
        $"last-write-cycle={machine.PrimaryI2c.LastWriteCycle}");
    throw;
}

Console.WriteLine($"instructions={machine.ExecutedInstructions:n0} stopped={machine.IsStopped} {machine.StopReason}");
Console.WriteLine(
    $"frames={machine.FrameVersion:n0} frame-hash=" +
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(machine.Frame.Span)).ToLowerInvariant());
Console.WriteLine(
    $"flash reads={machine.FlashMemory.ReadCount:n0} writes={machine.FlashMemory.WriteCount:n0} " +
    $"program={machine.FlashMemory.ProgramWordCount:n0} " +
    $"erase={machine.FlashMemory.EraseConfirmCount:n0} lock={machine.FlashMemory.LockConfirmCount:n0}");
Console.WriteLine(
    $"final pc={machine.Cpu.PC:x6} sp={machine.Cpu.SP:x4} sreg={machine.Cpu.SREG:x2} " +
    $"next-irq={machine.Cpu.NextInterrupt:x} max-irq={machine.Cpu.MaxInterrupt:x} " +
    $"scheduler={Convert.ToHexString(machine.Cpu.Data.AsSpan(0xf600, 16))}");
foreach (var target in new[] { 0x6952, 0xcf0a, 0xdb93, 0xe5b1 })
{
    foreach (var context in AsicRom.GetSchedulerContexts(machine.Cpu))
    {
        var descriptor = context.DescriptorAddress;
        var bank = machine.Cpu.Data[descriptor + 35] << 16;
        var floor = bank | machine.Cpu.GetUint16(descriptor + 51);
        var top = bank | machine.Cpu.GetUint16(descriptor + 37);
        if ((uint)(target - (floor & 0xffff)) <=
            (uint)((top & 0xffff) - (floor & 0xffff)))
        {
            Console.WriteLine(
                $"WINDOW target={target:x4} process={context.Process:x2} " +
                $"context={context.Address:x6} descriptor={descriptor:x6} " +
                $"task={context.TaskEntry:x6} physical={floor:x6}..{top:x6}");
        }
    }
}
foreach (var context in AsicRom.GetSchedulerContexts(machine.Cpu)
             .Where(context => machine.Cpu.Data[context.DescriptorAddress + 10] != 0))
{
    Console.WriteLine(
        $"SPECIAL-CONTEXT process={context.Process:x2} context={context.Address:x6} " +
        $"descriptor={context.DescriptorAddress:x6} " +
        $"type={machine.Cpu.Data[context.DescriptorAddress + 10]:x2}");
}
if (Environment.GetEnvironmentVariable("DUMP_CONTEXTS") == "1")
{
    var contexts = AsicRom.GetSchedulerContexts(machine.Cpu)
        .OrderBy(context => context.Process)
        .ToArray();
    var hardwareAllocations = new List<(int Floor, int Top, byte Process)>();
    foreach (var context in contexts)
    {
        var descriptor = context.DescriptorAddress;
        var bank = machine.Cpu.Data[descriptor + 35] << 16;
        var floor = bank | machine.Cpu.GetUint16(descriptor + 51);
        var top = bank | machine.Cpu.GetUint16(descriptor + 37);
        var hardwareFloor = machine.Cpu.GetUint16(descriptor + 33);
        var hardwareTop = machine.Cpu.GetUint16(descriptor + 31);
        if (hardwareTop != context.Address - 2 ||
            hardwareFloor > context.Address - 3 ||
            context.HardwareStackStart < hardwareFloor ||
            context.HardwareStackStart + context.HardwareStack.Length - 1 > context.Address - 3)
        {
            throw new InvalidOperationException(
                $"Invalid hardware-stack allocation for process 0x{context.Process:x2}.");
        }
        hardwareAllocations.Add((hardwareFloor, hardwareTop, context.Process));
        Console.WriteLine(
            $"CONTEXT p={context.Process:x2} ctx={context.Address:x4} " +
            $"desc={descriptor:x4} entry={context.TaskEntry:x6} " +
            $"floor={floor:x6} top={top:x6} size={top - floor + 1:x} " +
            $"hw-allocation={hardwareFloor:x4}..{hardwareTop:x4} " +
            $"saved-pc={context.SavedPc:x6} saved-sp={context.SavedStackPointer:x4} " +
            $"y={context.SoftwareStackPointer:x6} ramps=" +
            $"{context.RampD:x2}/{context.RampX:x2}/{context.RampZ:x2} " +
            $"snapshot={context.HardwareStackStart:x4}+{context.HardwareStack.Length:x}");
    }
    var orderedAllocations = hardwareAllocations.OrderBy(allocation => allocation.Floor).ToArray();
    for (var index = 1; index < orderedAllocations.Length; index++)
    {
        if (orderedAllocations[index - 1].Top >= orderedAllocations[index].Floor)
        {
            throw new InvalidOperationException(
                $"Overlapping hardware stacks for processes " +
                $"0x{orderedAllocations[index - 1].Process:x2} and " +
                $"0x{orderedAllocations[index].Process:x2}.");
        }
    }
}
observer.Report();
var restoreInfo = AsicRom.GetSchedulerRestoreInfo(machine.Cpu);
Console.WriteLine(
    $"restore-paths captured={restoreInfo.CapturedInterruptRestores:n0} " +
    $"rejected-uncaptured={restoreInfo.RejectedUncapturedInterruptRestores:n0}");
Console.WriteLine($"interrupt-transitions={interruptTransitionCount:n0}");
foreach (var transition in interruptTransitions)
{
    Console.WriteLine(transition);
}

sealed class SchedulerObserver(bool brief) : IMiaExecutionObserver
{
    static readonly int[] Entries = [0x3f015c, 0x3f015e, 0x3f0160, 0x3f0162, 0x3f0164];
    readonly Dictionary<int, long> _counts = Entries.ToDictionary(entry => entry, _ => 0L);
    readonly Dictionary<int, long[]> _contextDifferences = Entries.ToDictionary(entry => entry, _ => new long[26]);
    readonly Dictionary<int, long[]> _romContextWrites = Entries.ToDictionary(entry => entry, _ => new long[26]);
    readonly Dictionary<int, long[]> _romDescriptorWrites = Entries.ToDictionary(entry => entry, _ => new long[40]);
    readonly Dictionary<int, long> _descriptorPending = Entries.ToDictionary(entry => entry, _ => 0L);
    readonly Dictionary<int, long> _contextPending = Entries.ToDictionary(entry => entry, _ => 0L);
    readonly Dictionary<int, long> _descriptorState = Entries.ToDictionary(entry => entry, _ => 0L);
    readonly Dictionary<int, long> _stateMismatch = Entries.ToDictionary(entry => entry, _ => 0L);
    readonly Dictionary<int, List<string>> _samples = Entries.ToDictionary(entry => entry, _ => new List<string>());
    readonly Dictionary<int, List<string>> _anomalies = Entries.ToDictionary(entry => entry, _ => new List<string>());
    readonly Dictionary<int, List<string>> _pendingSamples = Entries.ToDictionary(entry => entry, _ => new List<string>());
    readonly Dictionary<int, PendingIrq> _pendingIrqs = [];
    readonly List<string> _pendingIrqOutcomes = [];
    readonly HashSet<int> _firstContexts = [];
    readonly Dictionary<(byte Type, int ResumePc, int SpDelta), int> _initialFrames = [];
    readonly Dictionary<(int ReturnPc, int ParentReturnPc, int GrandparentReturnPc, byte Process, byte RampD), long> _delayCallers = [];
    readonly List<string> _traceStatusReads = [];
    readonly Queue<string> _transitionRing = [];
    readonly Queue<string> _pcHistory = [];
    readonly List<string> _watchSamples = [];
    long _queueEmptyDescriptor;
    long _queueMoveIntoEmpty;
    long _queueAppendToNonempty;
    long _queueInvalidDescriptor;
    long _queueInvalidContext;
    int _launchHits;
    int _watchHits;
    int _lastMappedAddress = -1;
    Snapshot? _before;

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("TRACE_MAPPING_ADDRESS"),
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var logicalMappingAddress))
        {
            var mappedAddress = cpu.TranslateDataAddress(logicalMappingAddress);
            if (mappedAddress != _lastMappedAddress)
            {
                Console.WriteLine(
                    $"MAPPING-CHANGE i={machine.ExecutedInstructions} c={machine.Cycles} " +
                    $"pc={cpu.PC:x6} p={cpu.ReadData(0xf606):x2} " +
                    $"active={cpu.GetUint16(0xf608):x4} " +
                    $"logical={logicalMappingAddress:x6} physical={mappedAddress:x6}");
                _lastMappedAddress = mappedAddress;
            }
        }
        if (_pcHistory.Count == 96)
        {
            _pcHistory.Dequeue();
        }
        _pcHistory.Enqueue(
            $"PC i={machine.ExecutedInstructions} c={machine.Cycles} pc={cpu.PC:x6} " +
            $"sp={cpu.SP:x4} y={cpu.Data[0x5a]:x2}{cpu.Data[29]:x2}{cpu.Data[28]:x2} " +
            $"p={cpu.ReadData(0xf606):x2} sreg={cpu.SREG:x2}");
        if (int.TryParse(
                Environment.GetEnvironmentVariable("STOP_Y_BELOW"),
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var stopYBelow) &&
            byte.TryParse(
                Environment.GetEnvironmentVariable("STOP_Y_PROCESS"),
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var stopYProcess) &&
            cpu.ReadData(0xf606) == stopYProcess &&
            cpu.GetUint16(28) < stopYBelow &&
            cpu.Data[0x5a] is 0x02 or 0xd6)
        {
            foreach (var history in _pcHistory)
            {
                Console.WriteLine(history);
            }
            Console.WriteLine(
                $"Y-BELOW threshold={stopYBelow:x4} " +
                $"regs={Convert.ToHexString(cpu.Data.AsSpan(0, 32))} " +
                $"ext={Convert.ToHexString(cpu.Data.AsSpan(0x58, 8))}");
            return $"Y below {stopYBelow:x4} for process {stopYProcess:x2}";
        }
        var stopProcessText = Environment.GetEnvironmentVariable("STOP_PC_PROCESS");
        var processMatches = string.IsNullOrEmpty(stopProcessText) ||
            byte.TryParse(
                stopProcessText,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var stopProcess) &&
            cpu.ReadData(0xf606) == stopProcess;
        if (processMatches && int.TryParse(
                Environment.GetEnvironmentVariable("STOP_PC"),
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var stopPc) &&
            cpu.PC == stopPc)
        {
            _watchHits++;
            if (_watchSamples.Count < 96)
            {
                var node = cpu.Data[4] | cpu.Data[5] << 8 | cpu.Data[6] << 16;
                var nodeBytes = node >= 0 && node + 15 < cpu.Data.Length
                    ? Convert.ToHexString(cpu.Data.AsSpan(node, 16))
                    : "out-of-range";
                var x = cpu.Data[26] | cpu.Data[27] << 8 | cpu.Data[0x59] << 16;
                var xBytes = x >= 0 && x + 15 < cpu.Data.Length
                    ? Convert.ToHexString(cpu.Data.AsSpan(x, 16))
                    : "out-of-range";
                var z = cpu.Data[30] | cpu.Data[31] << 8 | cpu.Data[0x5b] << 16;
                var zBytes = z >= 0 && z + 15 < cpu.Data.Length
                    ? Convert.ToHexString(cpu.Data.AsSpan(z, 16))
                    : "out-of-range";
                _watchSamples.Add(
                    $"WATCH-SAMPLE hit={_watchHits} i={machine.ExecutedInstructions} " +
                    $"c={machine.Cycles} p={cpu.ReadData(0xf606):x2} sp={cpu.SP:x4} " +
                    $"active={cpu.GetUint16(0xf608):x4} " +
                    $"interrupted={cpu.GetUint16(0xf60e):x4} " +
                    $"y={cpu.Data[0x5a]:x2}{cpu.Data[29]:x2}{cpu.Data[28]:x2} " +
                    $"r4-6={cpu.Data[4]:x2}{cpu.Data[5]:x2}{cpu.Data[6]:x2} " +
                    $"node={node:x6}:{nodeBytes} " +
                    $"x={x:x6}:{xBytes} " +
                    $"z={z:x6}:{zBytes} " +
                    $"r16-23={Convert.ToHexString(cpu.Data.AsSpan(16, 8))} " +
                    $"db={Convert.ToHexString(cpu.Data.AsSpan(0x2dbd8, 0x28))}");
            }
            var requestedHit = int.TryParse(
                Environment.GetEnvironmentVariable("STOP_PC_HIT"),
                out var parsedHit)
                ? parsedHit
                : 1;
            if (_watchHits == requestedHit)
            {
                foreach (var history in _pcHistory)
                {
                    Console.WriteLine(history);
                }
                var first = Math.Max(0, cpu.SP - 16);
                var count = Math.Min(cpu.Data.Length - first, 96);
                var y = cpu.Data[28] | cpu.Data[29] << 8 | cpu.Data[0x5a] << 16;
                var yFirst = Math.Max(0, y - 32);
                var yCount = Math.Min(cpu.Data.Length - yFirst, 96);
                Console.WriteLine(
                    $"WATCH-HIT pc={stopPc:x6} hit={_watchHits} " +
                    $"regs={Convert.ToHexString(cpu.Data.AsSpan(0, 32))} " +
                    $"ext={Convert.ToHexString(cpu.Data.AsSpan(0x58, 8))}");
                Console.WriteLine(
                    $"WATCH-STACK {first:x6}:" + Convert.ToHexString(cpu.Data.AsSpan(first, count)));
                Console.WriteLine(
                    $"WATCH-Y {yFirst:x6}:" + Convert.ToHexString(cpu.Data.AsSpan(yFirst, yCount)));
                var x = cpu.Data[26] | cpu.Data[27] << 8 | cpu.Data[0x59] << 16;
                var physicalX = cpu.TranslateDataAddress(x);
                var physicalXFirst = Math.Max(0, physicalX - 32);
                var physicalXCount = Math.Min(cpu.Data.Length - physicalXFirst, 96);
                Console.WriteLine(
                    $"WATCH-X {x:x6}->{physicalX:x6} {physicalXFirst:x6}:" +
                    Convert.ToHexString(cpu.Data.AsSpan(physicalXFirst, physicalXCount)));
                var left = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
                var right = cpu.Data[20] | cpu.Data[21] << 8 | cpu.Data[22] << 16;
                var physicalRight = cpu.TranslateDataAddress(right);
                var leftPhysical = left & 0x7fffff;
                Console.WriteLine(
                    $"WATCH-ARGS left={left:x6} flash={leftPhysical:x6}:" +
                    Convert.ToHexString(cpu.ProgBytes.AsSpan(leftPhysical, 16)) +
                    $" right={right:x6}->{physicalRight:x6}:" +
                    Convert.ToHexString(cpu.Data.AsSpan(physicalRight, 16)));
                var d6X = 0xd60000 | (x & 0xffff);
                var d6XFirst = Math.Max(0, d6X - 32);
                var d6XCount = Math.Min(cpu.Data.Length - d6XFirst, 96);
                Console.WriteLine(
                    $"WATCH-D6X {d6XFirst:x6}:" +
                    Convert.ToHexString(cpu.Data.AsSpan(d6XFirst, d6XCount)));
                Console.WriteLine(
                    "WATCH-I2C-STATE " +
                    Convert.ToHexString(cpu.Data.AsSpan(0x028680, 0x30)));
                Console.WriteLine(
                    "WATCH-DB " + Convert.ToHexString(cpu.Data.AsSpan(0x2db70, 0xa0)));
                Console.WriteLine(
                    "WATCH-D6DB " + Convert.ToHexString(cpu.Data.AsSpan(0xd6db70, 0xa0)));
                Console.WriteLine(
                    $"WATCH-E4FE map={cpu.TranslateDataAddress(0x02e4fe):x6} " +
                    $"raw={Convert.ToHexString(cpu.Data.AsSpan(0x02e4e0, 0x40))} " +
                    $"d6={Convert.ToHexString(cpu.Data.AsSpan(0xd6e4e0, 0x40))}");
                var indirectTarget = cpu.Data[0] | cpu.Data[1] << 8 | cpu.Data[2] << 16;
                var indirectDataFirst = Math.Max(0, indirectTarget - 16);
                var indirectDataCount = Math.Min(
                    cpu.Data.Length - indirectDataFirst,
                    64);
                Console.WriteLine(
                    $"WATCH-INDIRECT target={indirectTarget:x6} " +
                    $"data={indirectDataFirst:x6}:" +
                    Convert.ToHexString(cpu.Data.AsSpan(indirectDataFirst, indirectDataCount)));
                var activeContext = cpu.ReadData(0xf608) | cpu.ReadData(0xf609) << 8;
                var contextFirst = Math.Max(0, activeContext - 96);
                var contextCount = Math.Min(cpu.Data.Length - contextFirst, 256);
                Console.WriteLine(
                    $"WATCH-CONTEXT {contextFirst:x6}:" +
                    Convert.ToHexString(cpu.Data.AsSpan(contextFirst, contextCount)));
                return $"watch pc {stopPc:x6} hit {_watchHits}";
            }
        }
        if (cpu.PC == 0x00175b)
        {
            _launchHits++;
            if (int.TryParse(Environment.GetEnvironmentVariable("STOP_LAUNCH"), out var stopLaunch) &&
                _launchHits == stopLaunch)
            {
                foreach (var history in _pcHistory)
                {
                    Console.WriteLine(history);
                }
                var first = Math.Max(0, cpu.SP - 16);
                var count = Math.Min(cpu.Data.Length - first, 64);
                Console.WriteLine(
                    $"LAUNCH-HIT {_launchHits} stack={first:x4}:" +
                    Convert.ToHexString(cpu.Data.AsSpan(first, count)));
                return $"launch hit {_launchHits}";
            }
        }
        if (cpu.PC == AsicInterruptController.HighPriorityVectorWord)
        {
            AddTransition(machine, "IRQ");
        }
        if (cpu.PC == 0x074055 && _traceStatusReads.Count < 16)
        {
            var rampD = cpu.Data[0x58];
            var effectiveAddress = 0x0ac0 | rampD << 16;
            _traceStatusReads.Add(
                $"TRACE-STATUS cycle={machine.Cycles} process={cpu.ReadData(0xf606):x2} " +
                $"r17={cpu.Data[17]:x2} rampd={rampD:x2} effective={effectiveAddress:x6} " +
                $"raw={cpu.Data[effectiveAddress]:x2} hooked={cpu.ReadData(effectiveAddress):x2}");
        }
        if (cpu.PC == 0x0188fe && cpu.SP + 3 < cpu.Data.Length)
        {
            var returnPc = cpu.ReadData(cpu.SP + 3) |
                           cpu.ReadData(cpu.SP + 2) << 8 |
                           cpu.ReadData(cpu.SP + 1) << 16;
            var parentReturnPc = cpu.SP + 6 < cpu.Data.Length
                ? cpu.ReadData(cpu.SP + 6) |
                  cpu.ReadData(cpu.SP + 5) << 8 |
                  cpu.ReadData(cpu.SP + 4) << 16
                : -1;
            var grandparentReturnPc = cpu.SP + 9 < cpu.Data.Length
                ? cpu.ReadData(cpu.SP + 9) |
                  cpu.ReadData(cpu.SP + 8) << 8 |
                  cpu.ReadData(cpu.SP + 7) << 16
                : -1;
            var process = cpu.ReadData(0xf606);
            var key = (returnPc, parentReturnPc, grandparentReturnPc, process, cpu.Data[0x58]);
            _delayCallers[key] = _delayCallers.GetValueOrDefault(key) + 1;
        }
        if (cpu.PC == 0x3f0138 &&
            Environment.GetEnvironmentVariable("TRACE_ROM_IO") == "1")
        {
            var source = cpu.Data[20] | cpu.Data[21] << 8 | cpu.Data[22] << 16;
            var translatedSource = cpu.TranslateDataAddress(source);
            var stack = cpu.Data[28] | cpu.Data[29] << 8 | cpu.Data[0x5a] << 16;
            var translatedStack = cpu.TranslateDataAddress(stack);
            var returnPc = cpu.SP + 3 < cpu.Data.Length
                ? cpu.Data[cpu.SP + 3] |
                  cpu.Data[cpu.SP + 2] << 8 |
                  cpu.Data[cpu.SP + 1] << 16
                : -1;
            var logicalBytes = source >= 0 && source + 15 < cpu.Data.Length
                ? Convert.ToHexString(cpu.Data.AsSpan(source, 16))
                : "out-of-range";
            var physicalBytes = translatedSource >= 0 && translatedSource + 15 < cpu.Data.Length
                ? Convert.ToHexString(cpu.Data.AsSpan(translatedSource, 16))
                : "out-of-range";
            Console.WriteLine(
                $"ROM-IO-TX i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"p={cpu.ReadData(0xf606):x2} return={returnPc:x6} " +
                $"dest={cpu.GetUint16(16):x4} source={source:x6}->{translatedSource:x6} " +
                $"stack={stack:x6}->{translatedStack:x6} " +
                $"count-logical={cpu.Data[stack]:x2} count-physical={cpu.Data[translatedStack]:x2} " +
                $"logical={logicalBytes} physical={physicalBytes}");
        }
        if (!Entries.Contains(cpu.PC))
        {
            return null;
        }

        var context = cpu.GetUint16(30) | cpu.Data[0x5b] << 16;
        // Native dispatch passes descriptor-31 as the active context.  The
        // descriptor pointer initially present at context-4 is part of the
        // startup hardware frame and is consumed by the 0x00221b trampoline,
        // so it is not a stable way to recover the descriptor after launch.
        var descriptor = context >= 0 && context + 31 + 39 < cpu.Data.Length
            ? context + 31
            : 0;
        var validContext = context >= 0 && context + 25 < cpu.Data.Length;
        var validDescriptor = descriptor > 0 && descriptor + 39 < cpu.Data.Length &&
            cpu.ReadData(descriptor + 5) < 0x80;
        var contextBytes = validContext ? cpu.Data.AsSpan(context, 26).ToArray() : [];
        var descriptorBytes = validDescriptor ? cpu.Data.AsSpan(descriptor, 40).ToArray() : [];
        var entry = cpu.PC;
        AddTransition(machine, $"ROM-{entry:x6}");
        _counts[entry]++;

        if (entry == 0x3f015c && validDescriptor && _firstContexts.Add(context))
        {
            var savedSp = descriptorBytes[18] | descriptorBytes[19] << 8;
            var resumePc = savedSp > 0 && savedSp + 9 < cpu.Data.Length
                ? cpu.ReadData(savedSp + 9) |
                  cpu.ReadData(savedSp + 8) << 8 |
                  cpu.ReadData(savedSp + 7) << 16
                : -1;
            var key = (descriptorBytes[10], resumePc, context - 3 - savedSp);
            _initialFrames[key] = _initialFrames.GetValueOrDefault(key) + 1;
        }
        if (entry == 0x3f015e && validContext && validDescriptor)
        {
            ObserveRestoreQueue(cpu, context, descriptor);
        }

        if (validDescriptor)
        {
            if (descriptorBytes[0] != 0xfd)
            {
                _descriptorPending[entry]++;
            }
            if (descriptorBytes[4] != 0)
            {
                _descriptorState[entry]++;
            }
            if (validContext)
            {
                if (contextBytes[0] != 0xfd)
                {
                    _contextPending[entry]++;
                }
                if (contextBytes[4] != descriptorBytes[4])
                {
                    _stateMismatch[entry]++;
                }
                for (var index = 0; index < contextBytes.Length; index++)
                {
                    if (contextBytes[index] != descriptorBytes[index])
                    {
                        _contextDifferences[entry][index]++;
                    }
                }
            }
        }

        if (_samples[entry].Count < 8)
        {
            var process = validDescriptor ? descriptorBytes[5] : (byte)0xff;
            var bitmap = validDescriptor
                ? cpu.ReadData(descriptorBytes[16] | descriptorBytes[17] << 8)
                : (byte)0;
            _samples[entry].Add(
                $"cycle={machine.Cycles} ctx={context:x6} desc={descriptor:x6} p={process:x2} " +
                $"active={cpu.ReadData(0xf608) | cpu.ReadData(0xf609) << 8:x4} " +
                $"sp={cpu.SP:x4} sreg={cpu.SREG:x2} bitmap={bitmap:x2} " +
                $"ctx={Convert.ToHexString(contextBytes)} desc={Convert.ToHexString(descriptorBytes)}");
        }
        if (validDescriptor && _anomalies[entry].Count < 16 &&
            (descriptorBytes[0] != 0xfd || descriptorBytes[4] != 0 ||
             validContext && contextBytes[4] != descriptorBytes[4]))
        {
            var process = descriptorBytes[5];
            _anomalies[entry].Add(
                $"ANOM cycle={machine.Cycles} ctx={context:x6} desc={descriptor:x6} p={process:x2} " +
                $"sp={cpu.SP:x4} sreg={cpu.SREG:x2} " +
                $"ctx={Convert.ToHexString(contextBytes)} desc={Convert.ToHexString(descriptorBytes)}");
        }
        if (validDescriptor && descriptorBytes[0] != 0xfd && _pendingSamples[entry].Count < 24)
        {
            var process = descriptorBytes[5];
            _pendingSamples[entry].Add(
                $"PENDING cycle={machine.Cycles} ctx={context:x6} desc={descriptor:x6} p={process:x2} " +
                $"pc-result-pending sp={cpu.SP:x4} sreg={cpu.SREG:x2} " +
                $"ctx={Convert.ToHexString(contextBytes)} desc={Convert.ToHexString(descriptorBytes)}");
        }
        if (entry == 0x3f0162 && validDescriptor && descriptorBytes[0] != 0xfd)
        {
            _pendingIrqs.TryAdd(context, new PendingIrq(machine.ExecutedInstructions, machine.Cycles));
        }
        if (entry == 0x3f0160 && validDescriptor && _pendingIrqs.Remove(context, out var pending) &&
            _pendingIrqOutcomes.Count < 40)
        {
            var bitmapAddress = descriptorBytes[16] | descriptorBytes[17] << 8;
            var bitmap = cpu.ReadData(bitmapAddress);
            _pendingIrqOutcomes.Add(
                $"IRQ->SAVE ctx={context:x6} p={descriptorBytes[5]:x2} " +
                $"di={machine.ExecutedInstructions - pending.Instruction:n0} dc={machine.Cycles - pending.Cycle:n0} " +
                $"resume={pending.ResumePc:x6} " +
                $"ctx-state={contextBytes[4]:x2} desc-state={descriptorBytes[4]:x2} " +
                $"ctx-head={contextBytes[0] | contextBytes[1] << 8:x4} " +
                $"desc-head={descriptorBytes[0] | descriptorBytes[1] << 8:x4} " +
                $"bitmap={bitmap:x2} mask={descriptorBytes[25]:x2}");
        }

        _before = new Snapshot(entry, context, descriptor, contextBytes, descriptorBytes);
        return null;
    }

    public void AfterRomDispatch(MiaMachine machine, int entry, AsicRomDispatchResult result)
    {
        if (_before is not { } before || before.Entry != entry)
        {
            return;
        }
        var cpu = machine.Cpu;
        AddTransition(machine, $"RET-{entry:x6}-{result}");
        if (entry == 0x3f0162 && _pendingIrqs.TryGetValue(before.Context, out var pendingIrq))
        {
            pendingIrq.ResumePc = cpu.PC;
        }
        if (before.ContextBytes.Length != 0 && before.Context + before.ContextBytes.Length <= cpu.Data.Length)
        {
            var after = cpu.Data.AsSpan(before.Context, before.ContextBytes.Length);
            for (var index = 0; index < after.Length; index++)
            {
                if (after[index] != before.ContextBytes[index])
                {
                    _romContextWrites[entry][index]++;
                }
            }
        }
        if (before.DescriptorBytes.Length != 0 && before.Descriptor + before.DescriptorBytes.Length <= cpu.Data.Length)
        {
            var after = cpu.Data.AsSpan(before.Descriptor, before.DescriptorBytes.Length);
            for (var index = 0; index < after.Length; index++)
            {
                if (after[index] != before.DescriptorBytes[index])
                {
                    _romDescriptorWrites[entry][index]++;
                }
            }
        }
        _before = null;
    }

    public void Report()
    {
        foreach (var sample in _watchSamples)
        {
            Console.WriteLine(sample);
        }
        foreach (var entry in Entries)
        {
            Console.WriteLine($"entry={entry:x6} count={_counts[entry]:n0} " +
                $"desc-pending={_descriptorPending[entry]:n0} ctx-pending={_contextPending[entry]:n0} " +
                $"desc-state={_descriptorState[entry]:n0} state-mismatch={_stateMismatch[entry]:n0}");
            Console.WriteLine("  ctx-vs-desc: " + Format(_contextDifferences[entry]));
            Console.WriteLine("  rom-ctx-writes: " + Format(_romContextWrites[entry]));
            Console.WriteLine("  rom-desc-writes: " + Format(_romDescriptorWrites[entry]));
            if (brief)
            {
                continue;
            }
            foreach (var sample in _samples[entry])
            {
                Console.WriteLine("  " + sample);
            }
            foreach (var sample in _anomalies[entry])
            {
                Console.WriteLine("  " + sample);
            }
            foreach (var sample in _pendingSamples[entry])
            {
                Console.WriteLine("  " + sample);
            }
        }
        foreach (var outcome in _pendingIrqOutcomes)
        {
            Console.WriteLine(outcome);
        }
        Console.WriteLine(
            $"RESTORE-QUEUES empty-descriptor={_queueEmptyDescriptor:n0} " +
            $"move-into-empty={_queueMoveIntoEmpty:n0} " +
            $"append-to-nonempty={_queueAppendToNonempty:n0} " +
            $"invalid-descriptor={_queueInvalidDescriptor:n0} " +
            $"invalid-context={_queueInvalidContext:n0}");
        foreach (var sample in _traceStatusReads)
        {
            Console.WriteLine(sample);
        }
        if (Environment.GetEnvironmentVariable("RING") == "1")
        {
            foreach (var transition in _transitionRing)
            {
                Console.WriteLine(transition);
            }
        }
        foreach (var (key, count) in _initialFrames.OrderBy(item => item.Key.Type).ThenBy(item => item.Key.ResumePc))
        {
            Console.WriteLine(
                $"INIT-FRAME type={key.Type:x2} pc={key.ResumePc:x6} " +
                $"top-minus-saved={key.SpDelta} count={count}");
        }
        foreach (var (key, count) in _delayCallers.OrderByDescending(item => item.Value))
        {
            Console.WriteLine(
                $"DELAY caller={key.ReturnPc:x6} parent={key.ParentReturnPc:x6} " +
                $"grandparent={key.GrandparentReturnPc:x6} process={key.Process:x2} " +
                $"rampd={key.RampD:x2} count={count:n0}");
        }
    }

    void ObserveRestoreQueue(AvrCore.Cpu cpu, int context, int descriptor)
    {
        var pendingHead = cpu.GetUint16(descriptor);
        if ((pendingHead & 0xff) == 0xfd)
        {
            _queueEmptyDescriptor++;
            return;
        }

        var pendingTail = cpu.GetUint16(descriptor + 2);
        if (pendingHead <= 0 || pendingHead + 1 >= cpu.Data.Length ||
            pendingTail <= 0 || pendingTail + 1 >= cpu.Data.Length ||
            (cpu.GetUint16(pendingTail) & 0xff) != 0xfd)
        {
            _queueInvalidDescriptor++;
            return;
        }

        var contextHead = cpu.GetUint16(context);
        if ((contextHead & 0xff) == 0xfd)
        {
            _queueMoveIntoEmpty++;
            return;
        }

        var contextTail = cpu.GetUint16(context + 2);
        if (contextHead <= 0 || contextHead + 1 >= cpu.Data.Length ||
            contextTail <= 0 || contextTail + 1 >= cpu.Data.Length ||
            (cpu.GetUint16(contextTail) & 0xff) != 0xfd)
        {
            _queueInvalidContext++;
            return;
        }
        _queueAppendToNonempty++;
    }

    static string Format(long[] counts) => string.Join(
        ' ',
        counts.Select((count, offset) => (count, offset))
            .Where(item => item.count != 0)
            .Select(item => $"+{item.offset}:{item.count}"));

    void AddTransition(MiaMachine machine, string kind)
    {
        var cpu = machine.Cpu;
        if (_transitionRing.Count == 256)
        {
            _transitionRing.Dequeue();
        }
        var context = cpu.ReadData(0xf608) | cpu.ReadData(0xf609) << 8;
        var priority = context > 0 && context + 11 < cpu.Data.Length
            ? cpu.ReadData(context + 11)
            : (byte)0xff;
        _transitionRing.Enqueue(
            $"SCHED {kind} i={machine.ExecutedInstructions} c={machine.Cycles} " +
            $"pc={cpu.PC:x6} sp={cpu.SP:x4} p={cpu.ReadData(0xf606):x2} " +
            $"ctx={context:x4} pri={priority:x2} nest={cpu.ReadData(0xf600):x2} " +
            $"request={cpu.ReadData(0xf60a):x2} sreg={cpu.SREG:x2}");
    }

    sealed record Snapshot(
        int Entry,
        int Context,
        int Descriptor,
        byte[] ContextBytes,
        byte[] DescriptorBytes);

    sealed class PendingIrq(long instruction, long cycle)
    {
        public long Instruction { get; } = instruction;
        public long Cycle { get; } = cycle;
        public int ResumePc { get; set; }
    }
}
