#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

var instructionLimit = args.Length > 0 && long.TryParse(args[0], out var parsedLimit)
    ? parsedLimit
    : 100_000_000;
byte? conversionProbe = args.Length > 1 && byte.TryParse(args[1], out var parsedProbe)
    ? parsedProbe
    : null;
bool sweepConversion = args.Contains("--sweep", StringComparer.Ordinal);
ushort rawSample = Environment.GetEnvironmentVariable("MIA_PROBE_RAW_SAMPLE") is { } rawText
    ? Convert.ToUInt16(rawText, 16)
    : (ushort)0x0800;

MiaMachine? machine = null;
var cell = new GsmCompatibilityCell(
    -49,
    rawSample,
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
    rfSignalSource: new AsicSingleChannelRfSource(-49, rawSample),
    fchSource: cell,
    channelDecoderSource: cell,
    channelEncoderSink: cell))
{
    var observer = new RssiSetterObserver(conversionProbe, sweepConversion);
    machine.ExecutionObserver = observer;
    machine.SetPowerKey(pressed: true);
    var powerReleased = false;

    while (!machine.IsStopped &&
           machine.ExecutedInstructions < instructionLimit &&
           observer.SetterHits < 3)
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
        $"registered={cell.Registered} setters={observer.SetterHits} " +
        $"level={machine.Cpu.Data[0x050817]} frames={machine.FrameVersion} " +
        $"stopped={machine.IsStopped} reason={machine.StopReason}");
}

sealed class RssiSetterObserver : IMiaExecutionObserver
{
    readonly byte? _conversionProbe;
    readonly bool _sweepConversion;
    byte[]? _conversionSnapshot;
    int _conversionCandidate;
    ushort _activeSignal;
    int _activePayload;
    long _activeReceiveCycle;
    int _previousPc;
    int _rssiSource = -1;
    byte _rssiFlags;
    byte _rssiLevel;

    public int SetterHits { get; private set; }

    public RssiSetterObserver(byte? conversionProbe, bool sweepConversion)
    {
        _conversionProbe = conversionProbe;
        _sweepConversion = sweepConversion;
    }

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.PC == 0x19c87f)
        {
            Console.WriteLine(
                $"RSSI-RXLEV i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"caller={_previousPc:x6} signed={(sbyte)cpu.Data[16]} " +
                $"r20={cpu.Data[20]:x2} r21={cpu.Data[21]:x2}");
        }

        if (cpu.PC == 0x19c7b6)
        {
            var measurementIndex = cpu.Data[16] | cpu.Data[17] << 8;
            var baseIndex = cpu.Data[20];
            Console.WriteLine(
                $"RSSI-MEASUREMENT-LOOKUP i={machine.ExecutedInstructions} " +
                $"c={machine.Cycles} caller={_previousPc:x6} " +
                $"r16={cpu.Data[16]:x2} r17={cpu.Data[17]:x2} " +
                $"r20={cpu.Data[20]:x2} " +
                $"base={(sbyte)cpu.ReadData(0x028e5f + baseIndex)} " +
                $"correction={(sbyte)cpu.ReadData(0xd20351 + Math.Min(measurementIndex, 255))}");
        }

        if (cpu.PC == 0x18e538)
        {
            var thresholds = Enumerable.Range(0, 8)
                .Select(i => cpu.ReadData(0xd46ad0 + i))
                .ToArray();
            Console.WriteLine(
                $"RSSI-QUANTIZE i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"caller={_previousPc:x6} " +
                $"raw={cpu.Data[16]} aux={cpu.Data[20]} current={cpu.Data[0x027e98]} " +
                $"thresholds={Convert.ToHexString(thresholds)} " +
                $"stack={Convert.ToHexString(cpu.Data.AsSpan(cpu.SP + 1, 12))}");
        }

        if (cpu.PC is 0x18e555 or 0x048219)
        {
            Console.WriteLine(
                $"RSSI-RECORD site={cpu.PC:x6} i={machine.ExecutedInstructions} " +
                $"c={machine.Cycles} level={cpu.Data[16]} target={cpu.Data[20]:x2}");
        }

        if (_rssiSource >= 0)
        {
            var flags = cpu.Data[_rssiSource + 0x72];
            var level = cpu.Data[_rssiSource + 0x74];
            if (flags != _rssiFlags || level != _rssiLevel)
            {
                Console.WriteLine(
                    $"RSSI-SOURCE-WRITE i={machine.ExecutedInstructions} " +
                    $"c={machine.Cycles} prev={_previousPc:x6} pc={cpu.PC:x6} " +
                    $"source={_rssiSource:x6} " +
                    $"flags={_rssiFlags:x2}->{flags:x2} " +
                    $"level={_rssiLevel}->{level}");
                _rssiFlags = flags;
                _rssiLevel = level;
            }
        }

        if (cpu.PC == 0x10bbd7)
        {
            var rssiArgument = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
            _rssiSource = cpu.Data[20] | cpu.Data[21] << 8 | cpu.Data[22] << 16;
            _rssiFlags = cpu.Data[_rssiSource + 0x72];
            _rssiLevel = cpu.Data[_rssiSource + 0x74];
            Console.WriteLine(
                $"RSSI-CALLBACK i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"model={_rssiSource:x6} flags={_rssiFlags:x2} " +
                $"argument={rssiArgument:x6} input={cpu.Data[rssiArgument + 2]} " +
                $"argument-bytes={Convert.ToHexString(cpu.Data.AsSpan(rssiArgument, 24))} " +
                $"stack={Convert.ToHexString(cpu.Data.AsSpan(cpu.SP + 1, 24))}");

            // TEMPORARY CONVERSION PROBE: substitute a requested source value
            // only for this callback invocation.
            // This identifies the firmware's raw-level-to-widget mapping and
            // is not a product behavior or acceptance path.
            if (_conversionProbe is byte probe)
            {
                cpu.Data[rssiArgument + 2] = probe;
            }
        }

        if (cpu.PC is 0x109b00 or 0x109b01 or 0x109b03 or 0x109b05)
        {
            Console.WriteLine(
                $"RSSI-MAP pc={cpu.PC:x6} r16={cpu.Data[16]:x2} " +
                $"r17={cpu.Data[17]:x2} r18={cpu.Data[18]:x2} " +
                $"r20={cpu.Data[20]:x2} r24={cpu.Data[24]:x2} " +
                $"z={cpu.Data[30] | cpu.Data[31] << 8:x4} " +
                $"rampz={cpu.Data[0x5b]:x2} sp={cpu.SP:x6}");
        }

        if (_sweepConversion && cpu.PC == 0x10bbeb && _conversionSnapshot is null)
        {
            _conversionSnapshot = cpu.Data.AsSpan(0, 0x10000).ToArray();
            _conversionCandidate = 0;
            cpu.Data[16] = 0;
            cpu.Data[24] = 0;
        }

        if (_sweepConversion && cpu.PC == 0x10bbed && _conversionSnapshot is not null)
        {
            Console.WriteLine(
                $"RSSI-MAP-VALUE input={_conversionCandidate} output={cpu.Data[24]}");
            if (_conversionCandidate == byte.MaxValue)
            {
                return "temporary RSSI conversion sweep complete";
            }

            _conversionCandidate++;
            _conversionSnapshot.CopyTo(cpu.Data, 0);
            cpu.Data[16] = (byte)_conversionCandidate;
            cpu.Data[24] = (byte)_conversionCandidate;
            cpu.PC = 0x10bbeb;
        }

        if (cpu.PC == 0x001838 && cpu.Data[20] == 0x63)
        {
            var arguments = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
            var physicalArguments = cpu.TranslateDataAddress(arguments);
            if ((uint)physicalArguments < (uint)(cpu.Data.Length - 2))
            {
                var record = cpu.ReadData(physicalArguments) |
                    cpu.ReadData(physicalArguments + 1) << 8;
                if ((uint)record < (uint)(cpu.Data.Length - 2))
                {
                    var signal = (ushort)(cpu.ReadData(record) |
                        cpu.ReadData(record + 1) << 8);
                    if ((signal & 0xff00) == 0x1600)
                    {
                        var count = Math.Min(32, cpu.Data.Length - record);
                        Console.WriteLine(
                            $"UI-POST c={machine.Cycles} site={_previousPc:x6} " +
                            $"source={cpu.ReadData(0xf606):x2} signal={signal:x4} " +
                            $"bytes={Convert.ToHexString(cpu.Data.AsSpan(record, count))}");
                    }
                }
            }
        }

        if (cpu.PC == 0x001912 && cpu.ReadData(0xf606) == 0x63)
        {
            var payload = cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
            if ((uint)payload < (uint)(cpu.Data.Length - 2))
            {
                _activePayload = payload;
                _activeSignal = (ushort)(cpu.Data[payload] | cpu.Data[payload + 1] << 8);
                _activeReceiveCycle = machine.Cycles;
                var count = Math.Min(32, cpu.Data.Length - payload);
                Console.WriteLine(
                    $"UI c={machine.Cycles} source={cpu.Data[payload - 1]:x2} " +
                    $"signal={_activeSignal:x4} " +
                    $"payload={payload:x6} bytes=" +
                    Convert.ToHexString(cpu.Data.AsSpan(payload, count)));
            }
        }

        if (cpu.PC == 0x09fc00)
        {
            SetterHits++;
            var stack = cpu.SP + 1;
            var stackBytes = stack >= 0 && stack < cpu.Data.Length
                ? Convert.ToHexString(cpu.Data.AsSpan(
                    stack,
                    Math.Min(32, cpu.Data.Length - stack)))
                : "";
            Console.WriteLine(
                $"SETTER hit={SetterHits} i={machine.ExecutedInstructions} " +
                $"c={machine.Cycles} caller={_previousPc:x6} process=" +
                $"{cpu.ReadData(0xf606):x2} new-level={cpu.Data[20]} " +
                $"old-level={cpu.Data[0x050817]} active-signal={_activeSignal:x4} " +
                $"receive-age={machine.Cycles - _activeReceiveCycle} " +
                $"payload={_activePayload:x6} stack={stackBytes}");
        }

        _previousPc = cpu.PC;
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
