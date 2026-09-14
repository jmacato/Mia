#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using System.Globalization;
using System.Security.Cryptography;
using Mia.Emulator;

const string StandbyHash =
    "79b62efe3a38514b728c96d9b30b1929c30921a34e9e41ec1849019c8c12614c";

bool traceQueues = args.Contains("--trace-queues", StringComparer.Ordinal);
string gdfsPath = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
    is { } requestedGdfs
    ? requestedGdfs
    : "images/T68i_Full_GDFS.compact.raw";
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes(gdfsPath),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
var uiObserver = new UiReceiveObserver();
if (traceQueues)
{
    machine.ExecutionObserver = uiObserver;
}

var frameVersion = 0;
Console.WriteLine("Booting to the trusted standby frame...");
RunUntil(200_000_000, stopAtStandby: true);
PrintState();
DumpFrame();
Console.WriteLine(
    "Commands: key NAME [post-run], run COUNT, dump, state, quit. " +
    "Names: up/down/left/right/center/yes/no/options/c/0..9/star/hash.");

while (Console.ReadLine() is string line)
{
    string[] parts = line.Split(
        ' ',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
        continue;
    }

    if (parts[0] is "quit" or "q")
    {
        break;
    }
    if (parts[0] == "state")
    {
        PrintState();
        continue;
    }
    if (parts[0] == "trace" && parts.Length == 2 &&
        parts[1] is "on" or "off")
    {
        machine.ExecutionObserver = parts[1] == "on" ? uiObserver : null;
        Console.WriteLine($"TRACE {parts[1]}");
        continue;
    }
    if (parts[0] == "timers")
    {
        PrintTimerWheel();
        continue;
    }
    if (parts[0] == "timers29")
    {
        PrintProcess29Timers();
        continue;
    }
    if (parts[0] == "contexts")
    {
        PrintSchedulerContexts();
        continue;
    }
    if (parts[0] == "context" && parts.Length == 2 &&
        byte.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte process))
    {
        PrintSchedulerContext(process);
        continue;
    }
    if (parts[0] == "dump")
    {
        DumpFrame();
        continue;
    }
    if (parts[0] == "run" && parts.Length == 2 &&
        TryParseCount(parts[1], out long runCount))
    {
        RunUntil(runCount, stopAtStandby: false);
        PrintState();
        DumpFrame();
        continue;
    }
    if (parts[0] == "key" && parts.Length is 2 or 3 &&
        TryGetContact(parts[1], out Contact contact) &&
        (parts.Length == 2 || TryParseCount(parts[2], out _)))
    {
        long postRun = parts.Length == 3
            ? ParseCount(parts[2])
            : 8_000_000;
        Press(contact);
        RunUntil(900_000, stopAtStandby: false);
        Release(contact);
        RunUntil(postRun, stopAtStandby: false);
        PrintState();
        DumpFrame();
        continue;
    }

    Console.WriteLine($"Unrecognized command: {line}");
}

void Press(Contact contact)
{
    var result = contact.Power
        ? machine.SetPowerKey(true)
        : machine.SetKey(
            contact.ScanMask,
            contact.RowMask,
            contact.SecondaryScanMask,
            pressed: true);
    Console.WriteLine($"KEY {contact.Name} down {result}");
}

void Release(Contact contact)
{
    var result = contact.Power
        ? machine.SetPowerKey(false)
        : machine.SetKey(
            contact.ScanMask,
            contact.RowMask,
            contact.SecondaryScanMask,
            pressed: false);
    Console.WriteLine($"KEY {contact.Name} up {result}");
}

void RunUntil(long instructionCount, bool stopAtStandby)
{
    long end = machine.ExecutedInstructions + instructionCount;
    while (!machine.IsStopped && machine.ExecutedInstructions < end)
    {
        machine.RunWorkItems((int)Math.Min(65_536, end - machine.ExecutedInstructions));
        if (machine.FrameVersion == frameVersion)
        {
            continue;
        }

        frameVersion = machine.FrameVersion;
        string hash = FrameHash();
        Console.WriteLine(
            $"FRAME {frameVersion} i={machine.ExecutedInstructions} " +
            $"c={machine.Cycles} pc={machine.Cpu.PC:x6} " +
            $"process={machine.Cpu.Data[0xf606]:x2} hash={hash}");
        if (stopAtStandby && hash == StandbyHash)
        {
            return;
        }
    }
}

void PrintSchedulerContexts()
{
    foreach (var context in AsicRom.GetSchedulerContexts(machine.Cpu))
    {
        Console.WriteLine(
            $"CONTEXT process=0x{context.Process:x2} " +
            $"context=0x{context.Address:x6} descriptor=0x{context.DescriptorAddress:x6} " +
            $"saved=0x{context.SavedPc:x6} state=0x{context.State:x2} " +
            $"descriptor-state=0x{machine.Cpu.Data[context.DescriptorAddress + 4]:x2} " +
            $"links={machine.Cpu.Data[context.Address + 14]:x2}/" +
            $"{machine.Cpu.Data[context.Address + 15]:x2}:" +
            $"{machine.Cpu.Data[context.DescriptorAddress + 14]:x2}/" +
            $"{machine.Cpu.Data[context.DescriptorAddress + 15]:x2} " +
            $"runnable={context.Runnable}");
    }
}

void PrintSchedulerContext(byte process)
{
    var context = AsicRom.GetSchedulerContexts(machine.Cpu)
        .Single(item => item.Process == process);
    int filterLogical =
        machine.Cpu.Data[context.Address + 6] |
        machine.Cpu.Data[context.Address + 7] << 8 |
        machine.Cpu.Data[context.Address + 8] << 16;
    int filterPhysical = machine.Cpu.TranslateDataAddress(filterLogical);
    int filterLength = (uint)filterPhysical < (uint)machine.Cpu.Data.Length
        ? Math.Min(32, machine.Cpu.Data.Length - filterPhysical)
        : 0;
    Console.WriteLine(
        $"CONTEXT-DETAIL process=0x{context.Process:x2} " +
        $"context=0x{context.Address:x6} descriptor=0x{context.DescriptorAddress:x6} " +
        $"entry=0x{context.TaskEntry:x6} saved=0x{context.SavedPc:x6} " +
        $"state=0x{context.State:x2} descriptor-state=" +
        $"0x{machine.Cpu.Data[context.DescriptorAddress + 4]:x2} " +
        $"filter=0x{filterLogical:x6}->0x{filterPhysical:x6}:" +
        (filterLength == 0
            ? "out-of-range"
            : Convert.ToHexString(machine.Cpu.Data.AsSpan(filterPhysical, filterLength))));
    Console.WriteLine($"  registers={Convert.ToHexString(context.Registers.Span)}");
    Console.WriteLine(
        $"  hardware-stack=0x{context.HardwareStackStart:x6}:" +
        Convert.ToHexString(context.HardwareStack.Span));
}

void PrintState()
{
    byte process = machine.Cpu.ReadData(0xf606);
    var contexts = AsicRom.GetSchedulerContexts(machine.Cpu).ToArray();
    int runnable = contexts.Count(context => context.Runnable);
    Console.WriteLine(
        $"STATE i={machine.ExecutedInstructions} c={machine.Cycles} " +
        $"pc={machine.Cpu.PC:x6} process={process:x2} frames={machine.FrameVersion} " +
        $"runnable={runnable}/{contexts.Length} stopped={machine.IsStopped} " +
        $"suspicious-timer-insert={uiObserver.SuspiciousTimerInsertCount} " +
        $"key-post={uiObserver.KeyPostCount} " +
        $"ui-key-press={uiObserver.KeyPressCount} " +
        $"ui-key-release={uiObserver.KeyReleaseCount} " +
        $"flash-program={machine.FlashMemory.ProgramWordCount} " +
        $"flash-erase={machine.FlashMemory.EraseConfirmCount}");
    if (machine.Cpu.PC is >= 0x0021eb and <= 0x00221a)
    {
        PrintTimerWheel();
    }
}

void PrintTimerWheel()
{
    const int bucketTable = 0xf646;
    var descriptors = AsicRom.GetProcessDescriptors(machine.Cpu)
        .ToDictionary(item => item.Process);
    var memberships = new Dictionary<byte, List<int>>();
    int nonempty = 0;
    for (var bucket = 0; bucket < 256; bucket++)
    {
        byte process = machine.Cpu.ReadData(bucketTable + bucket);
        if (process == 0)
        {
            continue;
        }

        nonempty++;
        Console.WriteLine($"TIMER-BUCKET bucket=0x{bucket:x2} head=0x{process:x2}");
        var path = new List<byte>();
        var visited = new HashSet<byte>();
        while (process != 0 && descriptors.TryGetValue(process, out var descriptor))
        {
            if (!memberships.TryGetValue(process, out var buckets))
            {
                buckets = [];
                memberships.Add(process, buckets);
            }
            buckets.Add(bucket);
            path.Add(process);
            if (!visited.Add(process))
            {
                Console.WriteLine(
                    $"TIMER-CYCLE bucket=0x{bucket:x2} " +
                    $"path={string.Join("->", path.Select(item => $"{item:x2}"))}");
                break;
            }
            process = machine.Cpu.ReadData(descriptor.Address + 14);
        }
        if (process != 0 && !descriptors.ContainsKey(process))
        {
            Console.WriteLine(
                $"TIMER-UNKNOWN bucket=0x{bucket:x2} process=0x{process:x2} " +
                $"path={string.Join("->", path.Select(item => $"{item:x2}"))}");
        }
        else if (path.Count != 0)
        {
            Console.WriteLine(
                $"TIMER-PATH bucket=0x{bucket:x2} " +
                $"path={string.Join("->", path.Select(item => $"{item:x2}"))}");
        }
    }

    foreach (var (process, buckets) in memberships.Where(item => item.Value.Count > 1))
    {
        Console.WriteLine(
            $"TIMER-DUPLICATE process=0x{process:x2} " +
            $"buckets={string.Join(',', buckets.Select(item => $"{item:x2}"))}");
    }
    Console.WriteLine(
        $"TIMERS current=0x{machine.Cpu.ReadData(0xf623):x2} nonempty={nonempty}");
}

void PrintProcess29Timers()
{
    const int headAddress = 0x02735d;
    int translatedHead = machine.Cpu.TranslateDataAddress(headAddress);
    if ((uint)translatedHead > (uint)(machine.Cpu.Data.Length - 3))
    {
        Console.WriteLine(
            $"TIMER29 head=0x{headAddress:x6}->0x{translatedHead:x6}:out");
        return;
    }

    int node = machine.Cpu.Data[translatedHead] |
        machine.Cpu.Data[translatedHead + 1] << 8 |
        machine.Cpu.Data[translatedHead + 2] << 16;
    Console.WriteLine(
        $"TIMER29 head=0x{headAddress:x6}->0x{translatedHead:x6} node=0x{node:x6}");
    var visited = new HashSet<int>();
    for (var index = 0; node != 0 && node != 0x00fdfd && index < 64; index++)
    {
        int physical = machine.Cpu.TranslateDataAddress(node);
        if ((uint)physical > (uint)(machine.Cpu.Data.Length - 21))
        {
            Console.WriteLine(
                $"TIMER29-NODE index={index} node=0x{node:x6}->0x{physical:x6}:out");
            break;
        }
        if (!visited.Add(physical))
        {
            Console.WriteLine(
                $"TIMER29-CYCLE index={index} node=0x{node:x6}->0x{physical:x6}");
            break;
        }

        Console.WriteLine(
            $"TIMER29-NODE index={index} node=0x{node:x6}->0x{physical:x6} data=" +
            Convert.ToHexString(machine.Cpu.Data.AsSpan(physical, 21)));
        node = machine.Cpu.Data[physical + 3] |
            machine.Cpu.Data[physical + 4] << 8 |
            machine.Cpu.Data[physical + 5] << 16;
    }
}

void DumpFrame()
{
    string stem = $"/tmp/mia-stall-frame-{machine.FrameVersion:D4}";
    File.WriteAllBytes(stem + ".rgb332", machine.Frame.ToArray());
    using var output = File.Create(stem + ".ppm");
    using (var header = new StreamWriter(output, leaveOpen: true))
    {
        header.Write($"P6\n{S4595Display.Width} {S4595Display.Height}\n255\n");
    }
    Span<byte> rgb = stackalloc byte[3];
    foreach (byte pixel in machine.Frame.Span)
    {
        var expanded = S4595Display.ExpandRgb332(pixel);
        rgb[0] = expanded.Red;
        rgb[1] = expanded.Green;
        rgb[2] = expanded.Blue;
        output.Write(rgb);
    }
    Console.WriteLine($"DUMP {stem}.ppm hash={FrameHash()}");
}

string FrameHash() =>
    Convert.ToHexStringLower(SHA256.HashData(machine.Frame.Span));

static bool TryParseCount(string text, out long value)
{
    long multiplier = text.EndsWith("m", StringComparison.OrdinalIgnoreCase)
        ? 1_000_000
        : text.EndsWith("k", StringComparison.OrdinalIgnoreCase)
            ? 1_000
            : 1;
    string digits = multiplier == 1 ? text : text[..^1];
    return long.TryParse(
        digits,
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out value) &&
        (value *= multiplier) >= 0;
}

static long ParseCount(string text) =>
    TryParseCount(text, out long value)
        ? value
        : throw new FormatException(text);

static bool TryGetContact(string name, out Contact contact)
{
    contact = name.ToLowerInvariant() switch
    {
        "yes" => new(name, 0x0d, 0x01),
        "up" => new(name, 0x0b, 0x10, 0x07),
        "no" => new(name, 0x0f, 0x01, Power: true),
        "left" => new(name, 0x0d, 0x10, 0x0b),
        "center" or "ok" => new(name, 0x0f, 0x08),
        "right" => new(name, 0x0e, 0x10, 0x07),
        "options" or "opt" => new(name, 0x0d, 0x02),
        "down" => new(name, 0x0e, 0x10, 0x0d),
        "c" => new(name, 0x0f, 0x02),
        "1" => new(name, 0x07, 0x01),
        "2" => new(name, 0x0b, 0x01),
        "3" => new(name, 0x0e, 0x01),
        "4" => new(name, 0x07, 0x02),
        "5" => new(name, 0x0b, 0x02),
        "6" => new(name, 0x0e, 0x02),
        "7" => new(name, 0x07, 0x04),
        "8" => new(name, 0x0b, 0x04),
        "9" => new(name, 0x0e, 0x04),
        "star" or "*" => new(name, 0x07, 0x08),
        "0" => new(name, 0x0b, 0x08),
        "hash" or "#" => new(name, 0x0e, 0x08),
        _ => default,
    };
    return contact.Name is not null;
}

readonly record struct Contact(
    string Name,
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask = null,
    bool Power = false);

sealed class UiReceiveObserver : IMiaExecutionObserver
{
    const int Process29Context = 0x00c019;
    const int Process29Descriptor = Process29Context + 31;
    const int Process3aContext = 0x00ca98;
    const int Process3aDescriptor = Process3aContext + 31;
    const int Process3bContext = 0x00cb55;
    const int Process3bDescriptor = Process3bContext + 31;
    const int TraceCapacity = 4096;
    readonly Queue<string> _process29Trace = new();
    bool _process29TraceDumped;
    bool _persistentHeadPreviousInvariant;

    public long SuspiciousTimerInsertCount { get; private set; }

    public long KeyPostCount { get; private set; }

    public long KeyPressCount { get; private set; }

    public long KeyReleaseCount { get; private set; }

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        ObserveProcess3bReceiveScan(machine);
        ObserveProcess3bQueue(machine);
        ObserveProcess63Wait(machine);
        ObserveProcess29TimerPath(machine);
        var atomicityStop = ObserveTimerWheelAtomicity(machine);
        if (atomicityStop is not null)
        {
            return atomicityStop;
        }
        if (cpu.PC == 0x00201d)
        {
            var timerStop = ObserveTimerInsert(machine);
            if (timerStop is not null)
            {
                return timerStop;
            }
        }
        if (cpu.PC == 0x001838)
        {
            ObservePost(machine);
        }
        if (cpu.PC == 0x001912 && cpu.Data[0xf606] == 0x6b)
        {
            ObserveTargetReceive(machine, 0x6b);
        }
        if (cpu.PC == 0x001912 && cpu.Data[0xf606] == 0x3b)
        {
            ObserveTargetReceive(machine, 0x3b);
        }
        if (cpu.PC == 0x001912 && cpu.Data[0xf606] == 0x3a)
        {
            ObserveTargetReceive(machine, 0x3a);
        }
        if (cpu.PC == 0x001912)
        {
            ObserveAllReceive(machine);
        }
        if (cpu.PC != 0x001912)
        {
            return null;
        }

        int logicalPayload =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        int payload = cpu.TranslateDataAddress(logicalPayload);
        if ((uint)payload > (uint)(cpu.Data.Length - 6) ||
            cpu.Data[payload] != 0x48 || cpu.Data[payload + 1] != 0x26)
        {
            return null;
        }

        ushort subsignal = (ushort)(
            cpu.Data[payload + 2] | cpu.Data[payload + 3] << 8);
        ushort key = (ushort)(
            cpu.Data[payload + 4] | cpu.Data[payload + 5] << 8);
        if (subsignal == 0x00ab)
        {
            KeyPressCount++;
        }
        else if (subsignal == 0x00ac)
        {
            KeyReleaseCount++;
        }
        else
        {
            return null;
        }

        Console.WriteLine(
            $"UI-KEY c={machine.Cycles} " +
            $"process=0x{cpu.ReadData(0xf606):x2} " +
            $"{(subsignal == 0x00ab ? "down" : "up")} code=0x{key:x4}");
        return null;
    }

    string? ObserveTimerInsert(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        byte previousHead = cpu.Data[18];
        byte process = cpu.Data[19];
        int descriptor =
            cpu.Data[30] | cpu.Data[31] << 8 | cpu.Data[0x5b] << 16;
        byte descriptorProcess = (uint)(descriptor + 5) < (uint)cpu.Data.Length
            ? cpu.Data[descriptor + 5]
            : (byte)0xff;
        if (process == 0x29)
        {
            RecordProcess29(machine, $"insert previous=0x{previousHead:x2}");
            if (previousHead == process)
            {
                SuspiciousTimerInsertCount++;
                DumpProcess29Trace();
                Console.WriteLine(
                    $"TIMER-SELF-LINK c={machine.Cycles} process=0x{process:x2} " +
                    $"bucket=0x{cpu.Data[26] | cpu.Data[27] << 8:x4} " +
                    $"context=0x{descriptor:x6}");
                return "process 0x29 timer self-link";
            }
        }
        if (process != 0x80 && process != previousHead && descriptorProcess == process)
        {
            return null;
        }

        SuspiciousTimerInsertCount++;
        Console.WriteLine(
            $"TIMER-INSERT c={machine.Cycles} process=0x{process:x2} " +
            $"previous=0x{previousHead:x2} bucket=0x{cpu.Data[26] | cpu.Data[27] << 8:x4} " +
            $"descriptor=0x{descriptor:x6} descriptor-process=0x{descriptorProcess:x2} " +
            $"state=0x{cpu.Data[descriptor + 4]:x2} " +
            $"next=0x{cpu.Data[descriptor + 14]:x2} " +
            $"previous-link=0x{cpu.Data[descriptor + 15]:x2}");
        return null;
    }

    void ObserveProcess29TimerPath(MiaMachine machine)
    {
        if (KeyPressCount < 5)
        {
            return;
        }

        var cpu = machine.Cpu;
        int pc = cpu.PC;
        bool receiveBoundary = cpu.Data[0xf606] == 0x29 &&
            pc is 0x0019d8 or 0x0019dc or 0x0019e5 or 0x0019eb or
                0x001a05 or 0x001a08 or 0x00201d;
        bool unlinkBoundary = pc is >= 0x001a05 and <= 0x001a13;
        bool romBoundary = pc is 0x3f015e or 0x3f0160 or 0x3f0162 or 0x3f0164;
        bool tickBoundary = pc is 0x0021eb or 0x0021f4 or 0x002201 or
            0x002215 or 0x002217 or 0x00221a;
        if (!receiveBoundary && !unlinkBoundary && !romBoundary && !tickBoundary)
        {
            return;
        }
        RecordProcess29(machine, "before");
    }

    string? ObserveTimerWheelAtomicity(MiaMachine machine)
    {
        if (KeyPressCount < 5)
        {
            return null;
        }

        var cpu = machine.Cpu;
        bool process29IsHead = false;
        for (var index = 0; index < 256; index++)
        {
            if (cpu.Data[0xf646 + index] == 0x29)
            {
                process29IsHead = true;
                break;
            }
        }
        byte previous = cpu.Data[Process29Descriptor + 15];
        if (!process29IsHead || previous == 0)
        {
            if (_persistentHeadPreviousInvariant)
            {
                RecordProcess29(machine, "head-prev-invariant-resolved");
                _persistentHeadPreviousInvariant = false;
            }
            return null;
        }

        int pc = cpu.PC;
        byte current = cpu.Data[0xf606];
        if (current == 0x29 && pc != 0x3f015e)
        {
            // Insertion writes the active context first and mask-ROM save
            // publishes its new links to the inactive descriptor. Until that
            // save, the descriptor may legitimately retain the prior wait.
            return null;
        }
        bool nativeUnlinkWindow = pc is >= 0x001a08 and <= 0x001a11;
        bool interruptsEnabled = (cpu.SREG & 0x80) != 0;
        if (nativeUnlinkWindow && !interruptsEnabled)
        {
            return null;
        }

        if (!_persistentHeadPreviousInvariant)
        {
            RecordProcess29(
                machine,
                $"head-prev-invariant previous=0x{previous:x2}");
            _persistentHeadPreviousInvariant = true;
        }

        return null;
    }

    void RecordProcess29(MiaMachine machine, string phase)
    {
        var cpu = machine.Cpu;
        int active = cpu.Data[0xf608] | cpu.Data[0xf609] << 8;
        string activeFields = (uint)(active + 15) < (uint)cpu.Data.Length
            ? $"{cpu.Data[active + 4]:x2}/{cpu.Data[active + 14]:x2}/{cpu.Data[active + 15]:x2}"
            : "--/--/--";
        var buckets = new List<string>();
        for (var index = 0; index < 256; index++)
        {
            if (cpu.Data[0xf646 + index] == 0x29)
            {
                buckets.Add($"{index:x2}");
            }
        }
        int stackFirst = Math.Max(0, cpu.SP - 2);
        int stackLength = Math.Min(12, cpu.Data.Length - stackFirst);
        string line =
            $"c={machine.Cycles} pc={cpu.PC:x6} {phase} " +
            $"cur={cpu.Data[0xf606]:x2} active={active:x4}" +
            $"[{activeFields}] p29ctx={Process29Context:x4}" +
            $"[{cpu.Data[Process29Context + 4]:x2}/" +
            $"{cpu.Data[Process29Context + 14]:x2}/" +
            $"{cpu.Data[Process29Context + 15]:x2}] desc={Process29Descriptor:x4}" +
            $"[{cpu.Data[Process29Descriptor + 4]:x2}/" +
            $"{cpu.Data[Process29Descriptor + 14]:x2}/" +
            $"{cpu.Data[Process29Descriptor + 15]:x2}] " +
            $"heads={(buckets.Count == 0 ? "-" : string.Join(',', buckets))} " +
            $"sreg={cpu.SREG:x2} depth={cpu.Data[0xf600]:x2} " +
            $"resched={cpu.Data[0xf60a]:x2} " +
            $"x={cpu.Data[0x5a]:x2}:{cpu.Data[27]:x2}{cpu.Data[26]:x2} " +
            $"z={cpu.Data[0x5b]:x2}:{cpu.Data[31]:x2}{cpu.Data[30]:x2} " +
            $"sp={cpu.SP:x6} stk={Convert.ToHexString(cpu.Data.AsSpan(stackFirst, stackLength))}";
        _process29Trace.Enqueue(line);
        while (_process29Trace.Count > TraceCapacity)
        {
            _process29Trace.Dequeue();
        }
    }

    void DumpProcess29Trace()
    {
        if (_process29TraceDumped)
        {
            return;
        }
        _process29TraceDumped = true;
        const string path = "/tmp/mia-process29-timer-trace.log";
        File.WriteAllLines(path, _process29Trace);
        Console.WriteLine($"TIMER29-TRACE {path} lines={_process29Trace.Count}");
    }

    void ObservePost(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        int logicalArguments =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        int arguments = cpu.TranslateDataAddress(logicalArguments);
        if ((uint)arguments > (uint)(cpu.Data.Length - 2))
        {
            return;
        }

        int record = cpu.Data[arguments] | cpu.Data[arguments + 1] << 8;
        if ((uint)record > (uint)(cpu.Data.Length - 6))
        {
            return;
        }

        Console.WriteLine(
            $"SIGNAL-POST c={machine.Cycles} source=0x{cpu.Data[0xf606]:x2} " +
            $"destination=0x{cpu.Data[20]:x2} payload=0x{record:x4} " +
            $"type=0x{cpu.Data[record] | cpu.Data[record + 1] << 8:x4} data=" +
            Convert.ToHexString(cpu.Data.AsSpan(
                record,
                Math.Min(16, cpu.Data.Length - record))));

        if (cpu.Data[20] is 0x6b or 0x3b or 0x3a)
        {
            int count = Math.Min(24, cpu.Data.Length - record);
            Console.WriteLine(
                $"P{cpu.Data[20]:X2}-POST c={machine.Cycles} " +
                $"source=0x{cpu.Data[0xf606]:x2} " +
                $"record=0x{record:x6} data=" +
                Convert.ToHexString(cpu.Data.AsSpan(record, count)));
        }
        if (cpu.Data[record] != 0x48 || cpu.Data[record + 1] != 0x26)
        {
            return;
        }

        ushort subsignal = (ushort)(cpu.Data[record + 2] | cpu.Data[record + 3] << 8);
        ushort key = (ushort)(cpu.Data[record + 4] | cpu.Data[record + 5] << 8);
        if (subsignal is not (0x00ab or 0x00ac))
        {
            return;
        }
        KeyPostCount++;
        Console.WriteLine(
            $"KEY-POST c={machine.Cycles} source=0x{cpu.ReadData(0xf606):x2} " +
            $"destination=0x{cpu.Data[20]:x2} subsignal=0x{subsignal:x4} " +
            $"code=0x{key:x4}");
    }

    void ObserveProcess3bQueue(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        int pc = cpu.PC;
        int z = cpu.Data[30] | cpu.Data[31] << 8 | cpu.Data[0x5b] << 16;
        bool process3aSendBoundary = cpu.Data[20] == 0x3a && pc is
            0x001838 or 0x001852 or 0x00187a or 0x001882 or 0x001887 or
            0x001889 or 0x00188f or 0x0018a3 or 0x0018a5 or 0x0018a8;
        bool process3aReceiveBoundary = cpu.Data[0xf606] == 0x3a && pc is
            0x00191d or 0x00193e or 0x00194a or 0x00194c or 0x001912;
        bool process3aRomBoundary = pc is 0x3f015e or 0x3f0160 or 0x3f0162 or 0x3f0164 &&
            z == Process3aContext;
        if (process3aSendBoundary || process3aReceiveBoundary || process3aRomBoundary)
        {
            PrintProcessQueue(
                machine,
                0x3a,
                Process3aContext,
                Process3aDescriptor,
                $"before-{pc:x6}");
        }

        bool sendBoundary = cpu.Data[20] == 0x3b && pc is
            0x001838 or 0x001852 or 0x00187a or 0x001882 or 0x001887 or
            0x001889 or 0x00188f or 0x0018a3 or 0x0018a5 or 0x0018a8;
        bool receiveBoundary = cpu.Data[0xf606] == 0x3b && pc is
            0x00191d or 0x00193e or 0x00194a or 0x00194c or 0x001912;
        bool romBoundary = pc is 0x3f015e or 0x3f0160 or 0x3f0162 or 0x3f0164 &&
            z == Process3bContext;
        if (!sendBoundary && !receiveBoundary && !romBoundary)
        {
            return;
        }

        PrintProcess3bQueue(machine, $"before-{pc:x6}");
    }

    static void PrintProcessQueue(
        MiaMachine machine,
        byte process,
        int context,
        int descriptor,
        string phase)
    {
        var cpu = machine.Cpu;
        int active = cpu.Data[0xf608] | cpu.Data[0xf609] << 8;
        int bitmap = cpu.Data[descriptor + 16] | cpu.Data[descriptor + 17] << 8;
        byte mask = cpu.Data[descriptor + 25];
        byte bits = (uint)bitmap < (uint)cpu.Data.Length ? cpu.Data[bitmap] : (byte)0;
        Console.WriteLine(
            $"P{process:X2}-QUEUE c={machine.Cycles} {phase} cur={cpu.Data[0xf606]:x2} " +
            $"active=0x{active:x4} ctx={FormatQueue(cpu, context)} " +
            $"state=0x{cpu.Data[context + 4]:x2} desc={FormatQueue(cpu, descriptor)} " +
            $"descriptor-state=0x{cpu.Data[descriptor + 4]:x2} " +
            $"run=0x{bitmap:x4}:0x{bits:x2}&0x{mask:x2} " +
            $"filter={FormatFilter(cpu, context)} mutation=0x{cpu.Data[context + 39]:x2} " +
            $"nodes={FormatQueueNodes(cpu, context)}/{FormatQueueNodes(cpu, descriptor)}");
    }

    static void PrintProcess3bQueue(MiaMachine machine, string phase)
    {
        var cpu = machine.Cpu;
        int active = cpu.Data[0xf608] | cpu.Data[0xf609] << 8;
        int bitmap = cpu.Data[Process3bDescriptor + 16] |
            cpu.Data[Process3bDescriptor + 17] << 8;
        byte mask = cpu.Data[Process3bDescriptor + 25];
        byte bits = (uint)bitmap < (uint)cpu.Data.Length
            ? cpu.Data[bitmap]
            : (byte)0;
        string filter = FormatProcess3bFilter(cpu);
        Console.WriteLine(
            $"P3B-QUEUE c={machine.Cycles} {phase} cur={cpu.Data[0xf606]:x2} " +
            $"active=0x{active:x4} z=0x{cpu.Data[0x5b]:x2}{cpu.Data[31]:x2}{cpu.Data[30]:x2} " +
            $"ctx={FormatQueue(cpu, Process3bContext)} state=0x{cpu.Data[Process3bContext + 4]:x2} " +
            $"desc={FormatQueue(cpu, Process3bDescriptor)} state=0x{cpu.Data[Process3bDescriptor + 4]:x2} " +
            $"run=0x{bitmap:x4}:0x{bits:x2}&0x{mask:x2} " +
            $"filter={filter} mutation=0x{cpu.Data[Process3bContext + 39]:x2} " +
            $"nodes={FormatQueueNodes(cpu, Process3bContext)}/{FormatQueueNodes(cpu, Process3bDescriptor)}");
    }

    static string FormatProcess3bFilter(AvrCore.Cpu cpu)
        => FormatFilter(cpu, Process3bContext);

    static string FormatFilter(AvrCore.Cpu cpu, int context)
    {
        int logical = cpu.Data[context + 6] |
            cpu.Data[context + 7] << 8 |
            cpu.Data[context + 8] << 16;
        int physical = cpu.TranslateDataAddress(logical);
        int count = (uint)physical < (uint)cpu.Data.Length
            ? Math.Min(12, cpu.Data.Length - physical)
            : 0;
        return $"0x{logical:x6}->0x{physical:x6}:" +
            (count == 0
                ? "out"
                : Convert.ToHexString(Enumerable.Range(0, count)
                    .Select(offset => cpu.ReadData(physical + offset)).ToArray()));
    }

    static void ObserveProcess3bReceiveScan(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        int head = cpu.Data[Process3bContext] | cpu.Data[Process3bContext + 1] << 8;
        if (machine.Cycles < 270_000_000 || cpu.Data[0xf606] != 0x3b ||
            (head & 0xff) == 0xfd || cpu.PC is < 0x001926 or > 0x0019ac)
        {
            return;
        }

        Console.WriteLine(
            $"P3B-SCAN c={machine.Cycles} pc={cpu.PC:x6} op={cpu.GetProgWord(cpu.PC):x4} " +
            $"r16-23={Convert.ToHexString(cpu.Data.AsSpan(16, 8))} " +
            $"x={cpu.Data[0x59]:x2}:{cpu.Data[27]:x2}{cpu.Data[26]:x2} " +
            $"y={cpu.Data[0x5a]:x2}:{cpu.Data[29]:x2}{cpu.Data[28]:x2} " +
            $"z={cpu.Data[0x5b]:x2}:{cpu.Data[31]:x2}{cpu.Data[30]:x2} " +
            $"sreg={cpu.SREG:x2} queue={FormatQueue(cpu, Process3bContext)} " +
            $"filter={FormatProcess3bFilter(cpu)} mutation=0x{cpu.Data[Process3bContext + 39]:x2}");
    }

    static string FormatQueue(AvrCore.Cpu cpu, int address) =>
        $"0x{cpu.Data[address] | cpu.Data[address + 1] << 8:x4}/" +
        $"0x{cpu.Data[address + 2] | cpu.Data[address + 3] << 8:x4}";

    static string FormatQueueNodes(AvrCore.Cpu cpu, int address)
    {
        int node = cpu.Data[address] | cpu.Data[address + 1] << 8;
        var nodes = new List<string>();
        var visited = new HashSet<int>();
        while ((node & 0xff) != 0xfd && node > 0 && node + 1 < cpu.Data.Length &&
               visited.Add(node) && nodes.Count < 12)
        {
            int next = cpu.Data[node] | cpu.Data[node + 1] << 8;
            ushort type = node + 3 < cpu.Data.Length
                ? (ushort)(cpu.Data[node + 2] | cpu.Data[node + 3] << 8)
                : (ushort)0;
            nodes.Add($"{node:x4}:{type:x4}->{next:x4}");
            node = next;
        }
        if ((node & 0xff) != 0xfd)
        {
            nodes.Add(visited.Contains(node) ? $"cycle:{node:x4}" : $"end:{node:x4}");
        }
        return nodes.Count == 0 ? "-" : string.Join(',', nodes);
    }

    void ObserveTargetReceive(MiaMachine machine, byte process)
    {
        var cpu = machine.Cpu;
        int logicalPayload =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        int payload = cpu.TranslateDataAddress(logicalPayload);
        int count = (uint)payload < (uint)cpu.Data.Length
            ? Math.Min(24, cpu.Data.Length - payload)
            : 0;
        Console.WriteLine(
            $"P{process:X2}-RECV c={machine.Cycles} payload=0x{logicalPayload:x6}" +
            $"->0x{payload:x6} data=" +
            (count == 0
                ? "out-of-range"
                : Convert.ToHexString(cpu.Data.AsSpan(payload, count))));
    }

    static void ObserveAllReceive(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        int logicalPayload = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        if ((logicalPayload & 0xffff) == 0xfdfd)
        {
            return;
        }
        int payload = cpu.TranslateDataAddress(logicalPayload);
        if ((uint)payload > (uint)(cpu.Data.Length - 2))
        {
            return;
        }

        byte source = payload > 0 ? cpu.Data[payload - 1] : (byte)0xff;
        Console.WriteLine(
            $"SIGNAL-RECV c={machine.Cycles} process=0x{cpu.Data[0xf606]:x2} " +
            $"source=0x{source:x2} payload=0x{logicalPayload:x6}->0x{payload:x6} " +
            $"type=0x{cpu.Data[payload] | cpu.Data[payload + 1] << 8:x4} data=" +
            Convert.ToHexString(cpu.Data.AsSpan(
                payload,
                Math.Min(16, cpu.Data.Length - payload))));
    }

    static void ObserveProcess63Wait(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.Data[0xf606] != 0x63 || cpu.PC is not
            (0x07cec0 or 0x07cee2 or 0x07cef2 or 0x0019ae))
        {
            return;
        }

        int y = cpu.Data[28] | cpu.Data[29] << 8 | cpu.Data[0x5a] << 16;
        int physicalY = cpu.TranslateDataAddress(y);
        int count = (uint)physicalY < (uint)cpu.Data.Length
            ? Math.Min(24, cpu.Data.Length - physicalY)
            : 0;
        Console.WriteLine(
            $"P63-WAIT c={machine.Cycles} pc={cpu.PC:x6} " +
            $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
            $"y=0x{y:x6}->0x{physicalY:x6}:" +
            (count == 0
                ? "out"
                : Convert.ToHexString(cpu.Data.AsSpan(physicalY, count))));
        if (cpu.PC != 0x07cef2)
        {
            return;
        }

        int objectLogical = cpu.Data[8] | cpu.Data[9] << 8 | cpu.Data[10] << 16;
        int objectPhysical = cpu.TranslateDataAddress(objectLogical);
        if ((uint)objectPhysical >= (uint)cpu.Data.Length)
        {
            Console.WriteLine(
                $"P63-SYNC object=0x{objectLogical:x6}->0x{objectPhysical:x6}:out");
            return;
        }

        byte index = cpu.Data[objectPhysical];
        int slotLogical = 0x02e2a7 + 7 * index;
        int slotPhysical = cpu.TranslateDataAddress(slotLogical);
        int slotCount = (uint)slotPhysical < (uint)cpu.Data.Length
            ? Math.Min(14, cpu.Data.Length - slotPhysical)
            : 0;
        Console.WriteLine(
            $"P63-SYNC object=0x{objectLogical:x6}->0x{objectPhysical:x6}:" +
            Convert.ToHexString(cpu.Data.AsSpan(
                objectPhysical,
                Math.Min(8, cpu.Data.Length - objectPhysical))) +
            $" index=0x{index:x2} slot=0x{slotLogical:x6}->0x{slotPhysical:x6}:" +
            (slotCount == 0
                ? "out"
                : Convert.ToHexString(cpu.Data.AsSpan(slotPhysical, slotCount))));
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
        int z = machine.Cpu.Data[30] |
            machine.Cpu.Data[31] << 8 |
            machine.Cpu.Data[0x5b] << 16;
        if (entry is 0x3f015e or 0x3f0160 or 0x3f0162 or 0x3f0164 &&
            z == Process3aContext)
        {
            PrintProcessQueue(
                machine,
                0x3a,
                Process3aContext,
                Process3aDescriptor,
                $"after-{entry:x6}-{result}");
        }
        if (entry is 0x3f015e or 0x3f0160 or 0x3f0162 or 0x3f0164 &&
            z == Process3bContext)
        {
            PrintProcess3bQueue(machine, $"after-{entry:x6}-{result}");
        }
        if (KeyPressCount >= 5 && entry is 0x3f015e or 0x3f0160 or 0x3f0162 or 0x3f0164)
        {
            RecordProcess29(machine, $"after-{entry:x6}-{result}");
        }
    }
}
