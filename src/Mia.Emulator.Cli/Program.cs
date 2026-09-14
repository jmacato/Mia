// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using AvrCore;
using Mia.Emulator;

const int ProgramSize = 0x800000;
const int DataSize = 0x1000000;
const long DefaultInstructionLimit = 1_000_000;

// This CLI's stdout/stderr output is diagnostic text for the developer
// running it (hex dumps, instruction traces), never localized end-user UI;
// routing every call through here is what actually satisfies CA1303's
// intent (a single indirection point ready for a resource table) without
// wrapping every diagnostic string in a resource table for no real benefit.
static void PrintLine(string message) => Console.WriteLine(message);
static void PrintErrorLine(string message) => Console.Error.WriteLine(message);

var imagePath = "flat.bin";
var instructionLimit = DefaultInstructionLimit;
var simRxBursts = new List<(long Cycle, byte[] Bytes)>();
var incomingGsmSms = new List<(string Originator, string Text)>();
var incomingGsmCalls = new List<string>();
string? channel0TransmitPath = null;
string? channel1TransmitPath = null;
string? lcdCapturePath = null;
string? lcdFramePath = null;
string? lcdLiveFramePath = null;
string? audioWavPath = null;
string? asicFirmwarePath = null;
string? modemPath = null;
string? modemDebugPath = null;
string? modemUart1TransmitPath = null;
string? gdfsCapturePath = null;
var gdfsPaths = new List<string>();
string? flashUserOtpHex = null;
var analyzeRom = false;
var printSchedulerContexts = false;
var traceScheduler = false;
var printProcessDescriptors = false;
var dataRanges = new List<(int Start, int Length)>();
var traceEvents = false;
var traceGsmUplink = false;
var eventTraceByteCount = 32;
var tracedEventDestinations = new HashSet<byte>();
var watchedPcs = new HashSet<int>();
var watchedDataAddresses = new HashSet<int>();
var watchedReadAddresses = new HashSet<int>();
int? stopPc = null;
var stopPcHit = 1;
var stopPcSeen = 0;
int? stopRomCopySource = null;
int? traceStartPc = null;
var traceStartHit = 1;
var traceStartSeen = 0;
var traceInstructionCount = 0;
var traceHistoryCount = 0;
var pcHotspotCount = 0;
var idleFastForwardDiagnostics = false;
var cycleLocked = false;
var gdfsSectorHeaderFastPathEnabled = true;
byte? keypadScanMask = null;
byte? keypadSecondaryScanMask = null;
var keypadRowMask = (byte)0;
var keypadReleaseCycle = long.MaxValue;
var keypadPressCycle = 0L;
var scheduledKeyEvents = new List<(long Cycle, byte ScanMask, byte RowMask, byte? SecondaryScanMask, bool Pressed)>();
var scheduledPowerKeyEvents = new List<(long Cycle, bool Pressed)>();
var powerKeyPressCycle = long.MaxValue;
var powerKeyReleaseCycle = long.MaxValue;
var virtualSim = false;
var liveGsmNetwork = false;
var interactiveKeypad = false;
short? rfArfcn = null;
ushort? rfRawSample = null;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--max-instructions" when index + 1 < args.Length && long.TryParse(args[++index], out var value):
            instructionLimit = value;
            break;
        case "--simrx-burst" when index + 1 < args.Length:
            {
                var parts = args[++index].Split(':', 2);
                byte[] bytes;
                try
                {
                    bytes = parts.Length == 2 ? Convert.FromHexString(parts[1]) : [];
                }
                catch (FormatException)
                {
                    bytes = [];
                }
                if (parts.Length != 2 ||
                    !long.TryParse(parts[0], out var cycle) || cycle < 0 || bytes.Length == 0)
                {
                    PrintErrorLine("--simrx-burst must be CYCLE:HEX with a non-negative decimal cycle and non-empty bytes.");
                    return 2;
                }
                simRxBursts.Add((cycle, bytes));
                break;
            }
        case "--virtual-sim":
            virtualSim = true;
            break;
        case "--live-gsm":
            liveGsmNetwork = true;
            break;
        case "--gsm-incoming-sms" when index + 1 < args.Length:
            {
                var parts = args[++index].Split(':', 2);
                if (parts.Length != 2 ||
                    string.IsNullOrWhiteSpace(parts[0]) ||
                    string.IsNullOrWhiteSpace(parts[1]))
                {
                    PrintErrorLine(
                        "--gsm-incoming-sms must be ORIGINATOR:TEXT.");
                    return 2;
                }
                incomingGsmSms.Add((parts[0], parts[1]));
                break;
            }
        case "--gsm-incoming-call" when index + 1 < args.Length:
            if (string.IsNullOrWhiteSpace(args[index + 1]))
            {
                PrintErrorLine(
                    "--gsm-incoming-call must be a non-empty calling number.");
                return 2;
            }
            incomingGsmCalls.Add(args[++index]);
            break;
        case "--rf-arfcn" when index + 1 < args.Length:
            if (!short.TryParse(
                    args[++index],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedRfArfcn))
            {
                PrintErrorLine("--rf-arfcn must be a signed decimal integer.");
                return 2;
            }
            rfArfcn = parsedRfArfcn;
            break;
        case "--rf-raw-sample" when index + 1 < args.Length:
            if (!ushort.TryParse(
                    args[++index],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var parsedRfRawSample))
            {
                PrintErrorLine("--rf-raw-sample must be a 16-bit hexadecimal value.");
                return 2;
            }
            rfRawSample = parsedRfRawSample;
            break;
        case "--interactive-keypad":
            interactiveKeypad = true;
            break;
        case "--channel0-tx-file" when index + 1 < args.Length:
            channel0TransmitPath = args[++index];
            break;
        case "--channel1-tx-file" when index + 1 < args.Length:
            channel1TransmitPath = args[++index];
            break;
        case "--lcd-capture-file" when index + 1 < args.Length:
            lcdCapturePath = args[++index];
            break;
        case "--lcd-frame-file" when index + 1 < args.Length:
            lcdFramePath = args[++index];
            break;
        case "--lcd-live-frame-file" when index + 1 < args.Length:
            lcdLiveFramePath = args[++index];
            break;
        case "--audio-wav-file" when index + 1 < args.Length:
            audioWavPath = args[++index];
            break;
        case "--asic-firmware-file" when index + 1 < args.Length:
            asicFirmwarePath = args[++index];
            break;
        case "--modem-file" when index + 1 < args.Length:
            modemPath = args[++index];
            break;
        case "--modem-debug-file" when index + 1 < args.Length:
            modemDebugPath = args[++index];
            break;
        case "--modem-uart1-tx-file" when index + 1 < args.Length:
            modemUart1TransmitPath = args[++index];
            break;
        case "--gdfs-file" when index + 1 < args.Length:
            gdfsPaths.Add(args[++index]);
            break;
        case "--gdfs-capture-file" when index + 1 < args.Length:
            gdfsCapturePath = args[++index];
            break;
        case "--flash-user-otp" when index + 1 < args.Length:
            flashUserOtpHex = args[++index];
            break;
        case "--help":
        case "-h":
            PrintLine("Usage: dotnet run --project src/Mia.Emulator.Cli -- [flat.bin] [--modem-file MODEM.bih] [--modem-debug-file PATH] [--modem-uart1-tx-file PATH] [--gdfs-file IMAGE.big|IMAGE.sbn|IMAGE.raw ...] [--gdfs-capture-file PATH] [--flash-user-otp HEX16] [--max-instructions N] [--cycle-locked] [--disable-gdfs-sector-header-fast-path] [--virtual-sim] [--live-gsm] [--rf-arfcn N --rf-raw-sample HEX] [--gsm-incoming-sms ORIGINATOR:TEXT ...] [--gsm-incoming-call ORIGINATOR ...] [--gsm-uplink-trace] [--simrx-burst CYCLE:HEX ...] [--channel0-tx-file PATH] [--channel1-tx-file PATH] [--lcd-capture-file PATH] [--lcd-frame-file PATH] [--lcd-live-frame-file PATH] [--audio-wav-file PATH] [--asic-firmware-file PATH] [--keypad-scan-mask HEX [--keypad-secondary-scan-mask HEX] --keypad-row-mask HEX --keypad-press-cycle N --keypad-release-cycle N] [--keypad-event CYCLE:down|up:SCAN:ROW[:SECONDARY] ...] [--interactive-keypad] [--power-key-press-cycle N --power-key-release-cycle N] [--power-key-event CYCLE:down|up ...] [--scheduler-contexts] [--scheduler-trace] [--process-descriptors] [--event-trace] [--event-trace-destination HEX ...] [--event-trace-bytes N] [--watch-pc HEX] [--watch-data HEX] [--watch-read HEX] [--stop-pc HEX --stop-pc-hit N] [--stop-rom-copy-source HEX] [--trace-start-pc HEX --trace-start-hit N --trace-instructions N] [--trace-history N] [--pc-hotspots N] [--idle-fast-forward-diagnostics] [--data-hex START:LENGTH] [--analyze-rom]");
            return 0;
        case "--analyze-rom":
            analyzeRom = true;
            break;
        case "--scheduler-contexts":
            printSchedulerContexts = true;
            break;
        case "--scheduler-trace":
            traceScheduler = true;
            break;
        case "--process-descriptors":
            printProcessDescriptors = true;
            break;
        case "--event-trace":
            traceEvents = true;
            break;
        case "--gsm-uplink-trace":
            traceGsmUplink = true;
            break;
        case "--event-trace-bytes" when index + 1 < args.Length &&
            int.TryParse(args[++index], out var parsedEventTraceByteCount) && parsedEventTraceByteCount > 0:
            eventTraceByteCount = parsedEventTraceByteCount;
            break;
        case "--event-trace-destination" when index + 1 < args.Length &&
            byte.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var destination):
            traceEvents = true;
            tracedEventDestinations.Add(destination);
            break;
        case "--watch-pc" when index + 1 < args.Length &&
            int.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var watchedPc):
            watchedPcs.Add(watchedPc);
            break;
        case "--watch-data" when index + 1 < args.Length &&
            int.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var watchedDataAddress):
            watchedDataAddresses.Add(watchedDataAddress);
            break;
        case "--watch-read" when index + 1 < args.Length &&
            int.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var watchedReadAddress):
            watchedReadAddresses.Add(watchedReadAddress);
            break;
        case "--stop-pc" when index + 1 < args.Length &&
            int.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedStopPc):
            stopPc = parsedStopPc;
            break;
        case "--stop-pc-hit" when index + 1 < args.Length &&
            int.TryParse(args[++index], out var parsedStopPcHit) && parsedStopPcHit > 0:
            stopPcHit = parsedStopPcHit;
            break;
        case "--stop-rom-copy-source" when index + 1 < args.Length &&
            int.TryParse(
                args[++index],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsedStopRomCopySource) &&
            parsedStopRomCopySource is >= 0 and <= 0xffffff:
            stopRomCopySource = parsedStopRomCopySource;
            break;
        case "--trace-start-pc" when index + 1 < args.Length &&
            int.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedTraceStartPc):
            traceStartPc = parsedTraceStartPc;
            break;
        case "--trace-start-hit" when index + 1 < args.Length &&
            int.TryParse(args[++index], out var parsedTraceStartHit) && parsedTraceStartHit > 0:
            traceStartHit = parsedTraceStartHit;
            break;
        case "--trace-instructions" when index + 1 < args.Length &&
            int.TryParse(args[++index], out var parsedTraceInstructionCount) && parsedTraceInstructionCount > 0:
            traceInstructionCount = parsedTraceInstructionCount;
            break;
        case "--trace-history" when index + 1 < args.Length &&
            int.TryParse(args[++index], out var parsedTraceHistoryCount) && parsedTraceHistoryCount > 0:
            traceHistoryCount = parsedTraceHistoryCount;
            break;
        case "--pc-hotspots" when index + 1 < args.Length && int.TryParse(args[++index], out var parsedHotspotCount):
            pcHotspotCount = parsedHotspotCount;
            break;
        case "--idle-fast-forward-diagnostics":
            idleFastForwardDiagnostics = true;
            break;
        case "--cycle-locked":
            cycleLocked = true;
            break;
        case "--disable-gdfs-sector-header-fast-path":
            gdfsSectorHeaderFastPathEnabled = false;
            break;
        case "--keypad-scan-mask" when index + 1 < args.Length &&
            byte.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value):
            keypadScanMask = value;
            break;
        case "--keypad-secondary-scan-mask" when index + 1 < args.Length &&
            byte.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value):
            keypadSecondaryScanMask = value;
            break;
        case "--keypad-row-mask" when index + 1 < args.Length &&
            byte.TryParse(args[++index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value):
            keypadRowMask = value;
            break;
        case "--keypad-release-cycle" when index + 1 < args.Length &&
            long.TryParse(args[++index], out var value) && value >= 0:
            keypadReleaseCycle = value;
            break;
        case "--keypad-press-cycle" when index + 1 < args.Length &&
            long.TryParse(args[++index], out var value) && value >= 0:
            keypadPressCycle = value;
            break;
        case "--keypad-event" when index + 1 < args.Length &&
            TryParseScheduledKeyEvent(args[++index], out var scheduledKeyEvent):
            scheduledKeyEvents.Add(scheduledKeyEvent);
            break;
        case "--power-key-event" when index + 1 < args.Length &&
            TryParseScheduledPowerKeyEvent(args[++index], out var scheduledPowerKeyEvent):
            scheduledPowerKeyEvents.Add(scheduledPowerKeyEvent);
            break;
        case "--power-key-press-cycle" when index + 1 < args.Length &&
            long.TryParse(args[++index], out var value) && value >= 0:
            powerKeyPressCycle = value;
            break;
        case "--power-key-release-cycle" when index + 1 < args.Length &&
            long.TryParse(args[++index], out var value) && value >= 0:
            powerKeyReleaseCycle = value;
            break;
        case "--data-hex" when index + 1 < args.Length:
            {
                var parts = args[++index].Split(':', 2);
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start) ||
                    !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) ||
                    start < 0 || length <= 0 || (long)start + length > DataSize)
                {
                    PrintErrorLine("--data-hex must be START:LENGTH in hexadecimal within data memory.");
                    return 2;
                }
                dataRanges.Add((start, length));
                break;
            }
        default:
            if (args[index].StartsWith('-'))
            {
                PrintErrorLine($"Unknown option: {args[index]}");
                return 2;
            }
            imagePath = args[index];
            break;
    }
}

if (instructionLimit <= 0)
{
    PrintErrorLine("--max-instructions must be positive.");
    return 2;
}

if (pcHotspotCount < 0)
{
    PrintErrorLine("--pc-hotspots must be non-negative.");
    return 2;
}

if ((rfArfcn is null) != (rfRawSample is null) ||
    rfArfcn is short configuredRfArfcn &&
        configuredRfArfcn is < AsicRfFrontend.BandZeroFirstArfcn or
            > AsicRfFrontend.BandZeroLastArfcn)
{
    PrintErrorLine(
        "--rf-arfcn and --rf-raw-sample must be supplied together; " +
        $"the currently decoded ARFCN range is " +
        $"{AsicRfFrontend.BandZeroFirstArfcn}..{AsicRfFrontend.BandZeroLastArfcn}.");
    return 2;
}

if ((incomingGsmSms.Count != 0 || incomingGsmCalls.Count != 0) &&
    rfArfcn is null && !liveGsmNetwork)
{
    PrintErrorLine(
        "--gsm-incoming-sms and --gsm-incoming-call require either --live-gsm " +
        "or --rf-arfcn with --rf-raw-sample.");
    return 2;
}

if ((keypadScanMask is null) != (keypadRowMask == 0) ||
    keypadScanMask is byte configuredScanMask && configuredScanMask is not (0x07 or 0x0b or 0x0d or 0x0e or 0x0f) ||
    keypadSecondaryScanMask is byte configuredSecondaryScanMask &&
        (keypadScanMask is null ||
         keypadScanMask == 0x0f ||
         configuredSecondaryScanMask is not (0x07 or 0x0b or 0x0d or 0x0e) ||
         configuredSecondaryScanMask == keypadScanMask) ||
    keypadRowMask is not (0 or 0x01 or 0x02 or 0x04 or 0x08 or 0x10))
{
    PrintErrorLine(
        "--keypad-scan-mask and a single-bit --keypad-row-mask must be supplied together; " +
        "an optional distinct --keypad-secondary-scan-mask forms a physical chord.");
    return 2;
}

byte[] flashUserOtp;
try
{
    flashUserOtp = flashUserOtpHex is null
        ? []
        : Convert.FromHexString(flashUserOtpHex);
}
catch (FormatException)
{
    PrintErrorLine("Hexadecimal options must contain an even number of hexadecimal digits.");
    return 2;
}

if (flashUserOtp.Length is not (0 or AsicFlashMemory.UserProtectionRegisterLength))
{
    PrintErrorLine("--flash-user-otp must contain exactly 16 hexadecimal digits.");
    return 2;
}

if (!File.Exists(imagePath))
{
    PrintErrorLine($"Firmware image not found: {Path.GetFullPath(imagePath)}");
    PrintErrorLine("Regenerate flat.bin with tools/extract_sbn.py.");
    return 2;
}

var image = File.ReadAllBytes(imagePath);
if (image.Length > ProgramSize)
{
    PrintErrorLine($"Firmware image is too large: 0x{image.Length:x} bytes (maximum 0x{ProgramSize:x}).");
    return 2;
}

if (analyzeRom)
{
    RomCallOracle.Print(RomCallOracle.Analyze(image), Console.Out);
    return 0;
}

byte[] modemBih = [];
if (modemPath is not null)
{
    if (!File.Exists(modemPath))
    {
        PrintErrorLine($"ARM modem BIH not found: {Path.GetFullPath(modemPath)}");
        return 2;
    }

    try
    {
        modemBih = File.ReadAllBytes(modemPath);
        _ = ArmModemImage.ParseBih(modemBih);
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not load ARM modem BIH: {error.Message}");
        return 2;
    }
}

var program = new byte[ProgramSize];
Array.Fill(program, (byte)0xff);
image.CopyTo(program, 0);
var loadedGdfs = new List<(string Path, int Address, int Length)>();
foreach (var gdfsPath in gdfsPaths)
{
    if (!File.Exists(gdfsPath))
    {
        PrintErrorLine($"GDFS image not found: {Path.GetFullPath(gdfsPath)}");
        return 2;
    }
    try
    {
        var (address, length) = GdfsImage.Load(File.ReadAllBytes(gdfsPath), program);
        loadedGdfs.Add((gdfsPath, address, length));
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not load GDFS image: {error.Message}");
        return 2;
    }
}

MiaMachine? machine = null;
var defaultLiveGsmOptions = new MiaLiveGsmOptions();
var liveGsmOptions = liveGsmNetwork
    ? defaultLiveGsmOptions with
    {
        Arfcn = rfArfcn ?? defaultLiveGsmOptions.Arfcn,
        IdleRssiRawSample = rfRawSample ?? defaultLiveGsmOptions.IdleRssiRawSample,
    }
    : null;
var rfSignalSource = !liveGsmNetwork &&
    rfArfcn is short configuredArfcn &&
    rfRawSample is ushort configuredRawSample
    ? new AsicSingleChannelRfSource(configuredArfcn, configuredRawSample)
    : null;
var outgoingGsmRequests = new ConcurrentQueue<GsmOutgoingNetworkRequest>();
const int MaximumGsmUplinkTraceFrames = 4_096;
var gsmUplinkTraceFrames = new ConcurrentQueue<(long Cycle, byte FirmwareState, string Bytes)>();
var gsmUplinkTraceFrameCount = 0;
var gsmDownlinkTraceStates = new ConcurrentQueue<(long Cycle, byte FirmwareState)>();
var gsmDownlinkTraceStateCount = 0;
GsmCompatibilityCell? gsmCell = null;
if (!liveGsmNetwork &&
    rfArfcn is short cellArfcn && rfRawSample is ushort cellRawSample)
{
    gsmCell = new GsmCompatibilityCell(
        cellArfcn,
        cellRawSample,
        randomAccessReferenceSource: () =>
            machine is null
                ? null
                : MiaGsmRandomAccessAdapter.ReadPendingReference(machine.Cpu),
        outgoingNetworkRequest: request =>
        {
            outgoingGsmRequests.Enqueue(request);
            _ = gsmCell!.ResolveNetworkRequest(new(
                request.RequestId,
                GsmNetworkRequestDecision.Accept));
        },
        dedicatedUplinkFrameObserved: frame =>
        {
            if (traceGsmUplink &&
                Interlocked.Increment(ref gsmUplinkTraceFrameCount) <=
                    MaximumGsmUplinkTraceFrames)
            {
                gsmUplinkTraceFrames.Enqueue((
                    machine?.Clock.Cycles ?? 0,
                    frame.FirmwareState,
                    Convert.ToHexString(frame.Bytes.Span)));
            }
        },
        controlChannelDecodeObserved: firmwareState =>
        {
            if (traceGsmUplink &&
                Interlocked.Increment(ref gsmDownlinkTraceStateCount) <=
                    MaximumGsmUplinkTraceFrames)
            {
                gsmDownlinkTraceStates.Enqueue((
                    machine?.Clock.Cycles ?? 0,
                    firmwareState));
            }
        });
    foreach (var (originator, text) in incomingGsmSms)
    {
        gsmCell.QueueIncomingSms(originator, text);
    }
    foreach (var originator in incomingGsmCalls)
    {
        gsmCell.QueueIncomingCall(originator);
    }
}
using var machineLifetime = CreateMachine();
if (machineLifetime is null)
{
    return 2;
}
machine = machineLifetime;
machine.GdfsSectorHeaderFastPathEnabled = gdfsSectorHeaderFastPathEnabled;
if (pcHotspotCount > 0)
{
    // This is machine-owned profiling, deliberately not an execution
    // observer: the latter disables verified idle acceleration.
    machine.Diagnostics.StartExecutionHotspotProfiling();
}
if (idleFastForwardDiagnostics)
{
    machine.Diagnostics.EnableIdleFastForwardDiagnostics();
}

MiaMachine? CreateMachine()
{
    try
    {
        return new MiaMachine(
            program,
            modemBih: modemBih,
            flashUserOtp: flashUserOtp,
            virtualSim: virtualSim,
            powerPressedInitially: powerKeyPressCycle == 0,
            powerKeyReleaseCycle: powerKeyReleaseCycle,
            coreSchedulingMode: cycleLocked
                ? MiaCoreSchedulingMode.CycleLocked
                : MiaCoreSchedulingMode.CoarseParallel,
            rfSignalSource: rfSignalSource,
            fchSource: gsmCell,
            channelDecoderSource: gsmCell,
            channelEncoderSink: gsmCell,
            liveGsm: liveGsmOptions);
    }
    catch (Exception error) when (error is ArgumentException or InvalidDataException)
    {
        PrintErrorLine($"Could not initialize Mia machine: {error.Message}");
        return null;
    }
}

if (machine.LiveGsm is { } liveGsm)
{
    foreach (var (originator, text) in incomingGsmSms)
    {
        if (!liveGsm.TryQueueIncomingSms(originator, text, out var result))
        {
            PrintErrorLine($"Could not queue incoming SMS: {result}");
            return 2;
        }
    }
    foreach (var originator in incomingGsmCalls)
    {
        if (!liveGsm.TryQueueIncomingCall(originator, autoAnswer: false, out var result))
        {
            PrintErrorLine($"Could not queue incoming call: {result}");
            return 2;
        }
    }
}

var cpu = machine.Cpu;
var clock = machine.Clock;
var flashMemory = machine.FlashMemory;
var interruptController = machine.InterruptController;
var rtc = machine.Rtc;
var highPriorityRequests = machine.HighPriorityRequests;
var powerPortController = machine.PowerPorts;
var secondaryPortController = machine.SecondaryPorts;
var simInterface = machine.SimInterface;
var simCard = machine.SimCard;
var sedTimer = machine.SedTimer;
var interruptRouter = machine.InterruptRouter;
var commandPort = machine.CommandPort;
var timeGenerator = machine.TimeGenerator;
var rfFrontend = machine.RfFrontend;
var adc = machine.Adc;
var phCommandController = machine.PhCommandController;
var display = machine.Display;
var i2c = machine.PrimaryI2c;
var i2c1 = machine.DisplayI2c;
var byteChannels = machine.ByteChannels;
var modem = machine.Modem;

var simTransmitBytes = new ConcurrentQueue<byte>();
var simCommands = new ConcurrentQueue<byte[]>();
simInterface.ByteTransmitted += simTransmitBytes.Enqueue;
if (simCard is not null)
{
    simCard.CommandReceived += command => simCommands.Enqueue(command.ToArray());
}
var commandPortBytes = new ConcurrentQueue<byte>();
commandPort.ByteWritten += commandPortBytes.Enqueue;

if (keypadScanMask is byte configuredKeypadScanMask)
{
    machine.Diagnostics.ScheduleKeyAt(
        keypadPressCycle,
        configuredKeypadScanMask,
        keypadRowMask,
        keypadSecondaryScanMask,
        pressed: true,
        raiseInterrupt: keypadPressCycle != 0);
    if (keypadReleaseCycle != long.MaxValue)
    {
        machine.Diagnostics.ScheduleKeyAt(
            keypadReleaseCycle,
            configuredKeypadScanMask,
            keypadRowMask,
            keypadSecondaryScanMask,
            pressed: false);
    }
}
foreach (var scheduledKeyEvent in scheduledKeyEvents.OrderBy(keyEvent => keyEvent.Cycle))
{
    machine.Diagnostics.ScheduleKeyAt(
        scheduledKeyEvent.Cycle,
        scheduledKeyEvent.ScanMask,
        scheduledKeyEvent.RowMask,
        scheduledKeyEvent.SecondaryScanMask,
        scheduledKeyEvent.Pressed,
        raiseInterrupt: scheduledKeyEvent.Pressed);
}
foreach (var scheduledPowerKeyEvent in scheduledPowerKeyEvents.OrderBy(keyEvent => keyEvent.Cycle))
{
    machine.Diagnostics.SchedulePowerKeyAt(
        scheduledPowerKeyEvent.Cycle,
        scheduledPowerKeyEvent.Pressed);
}
if (powerKeyPressCycle > 0 && powerKeyPressCycle != long.MaxValue)
{
    machine.Diagnostics.SchedulePowerKeyAt(powerKeyPressCycle, pressed: true);
}

if (interactiveKeypad)
{
    machine.InteractiveInput.DeferredReleaseApplied += contact =>
    {
        var name = contact.Power
            ? "power"
            : $"key scan=0x{contact.ScanMask:x2}" +
                (contact.SecondaryScanMask is byte secondary
                    ? $"+0x{secondary:x2}"
                    : string.Empty) +
                $" row=0x{contact.RowMask:x2}";
        PrintLine(
            $"Keypad input: cycle={machine.Cycles} {name} up " +
            "applied after firmware MMIO observation");
    };
    var inputThread = new Thread(() =>
    {
        while (Console.In.ReadLine() is { } command)
        {
            HandleInteractiveKeypadCommand(machine, command);
        }
    })
    {
        IsBackground = true,
        Name = "interactive keypad input",
    };
    inputThread.Start();
}

using var liveFramebuffer = lcdLiveFramePath is null
    ? null
    : new LiveFramebufferFile(lcdLiveFramePath);
liveFramebuffer?.Publish(machine.Frame.Span);
if (liveFramebuffer is not null)
{
    machine.FramePublished += frame => liveFramebuffer.Publish(frame.Span);
}
var lcdCapture = lcdCapturePath is null ? null : new ConcurrentQueue<byte>();
ReadOnlyMemory<byte> lastLcdFrame = machine.Frame;
var lastLcdScanRow = (Cycle: 0L, Pc: 0, SchedulerRecord: 0, DmaCycle: 0L, DmaPc: 0,
    DmaSource: 0, DmaLength: 0, PayloadLength: 0);
machine.Diagnostics.DisplayTransactionObserved += observation =>
{
    if (lcdCapture is not null)
    {
        lcdCapture.Enqueue(observation.Address);
        lcdCapture.Enqueue((byte)(observation.Payload.Length & 0xff));
        lcdCapture.Enqueue((byte)(observation.Payload.Length >> 8));
        foreach (var value in observation.Payload.Span)
        {
            lcdCapture.Enqueue(value);
        }
    }
    if (observation.Address == S4595Display.WriteAddress &&
        observation.Payload.Length > 1 &&
        observation.Payload.Span[0] == S4595Display.ScanRowCommand)
    {
        lastLcdFrame = observation.Frame;
        lastLcdScanRow = (
            observation.Cycle,
            observation.Pc,
            observation.SchedulerRecord,
            observation.DmaCycle,
            observation.DmaPc,
            observation.DmaSource,
            observation.DmaLength,
            observation.Payload.Length);
    }
};
var modemTransmitBytes = new ConcurrentQueue<byte>();
var modemReceiveBytes = new ConcurrentQueue<byte>();
var modemReadBytes = new ConcurrentQueue<byte>();
var modemDebugBytes = new ConcurrentQueue<byte>();
if (modem is not null)
{
    modem.Uart1ByteTransmitted += modemTransmitBytes.Enqueue;
    modem.Bus.Uart1ByteReceived += modemReceiveBytes.Enqueue;
    modem.Bus.Uart1ByteRead += modemReadBytes.Enqueue;
    modem.Bus.Uart2ByteTransmitted += modemDebugBytes.Enqueue;
}
var watchedDataWrites = new ConcurrentQueue<MiaDataWriteObservation>();
var watchedDataReads = new ConcurrentQueue<MiaDataReadObservation>();
foreach (var address in watchedDataAddresses)
{
    if (address < 0 || address >= DataSize)
    {
        PrintErrorLine($"Watched data address 0x{address:x} is outside data memory.");
        return 2;
    }
    machine.Diagnostics.InstallDataWriteProbe(address, watchedDataWrites.Enqueue);
}
foreach (var address in watchedReadAddresses)
{
    if (address < 0 || address >= DataSize)
    {
        PrintErrorLine($"Watched data address 0x{address:x} is outside data memory.");
        return 2;
    }
    machine.Diagnostics.InstallDataReadProbe(address, watchedDataReads.Enqueue);
}
var channel0TransmitBytes = channel0TransmitPath is null ? null : new ConcurrentQueue<byte>();
var channel1TransmitBytes = channel1TransmitPath is null ? null : new ConcurrentQueue<byte>();
byteChannels.ByteTransmitted += (channel, value) =>
{
    if (channel == 0)
    {
        channel0TransmitBytes?.Enqueue(value);
    }
    if (channel == 1)
    {
        channel1TransmitBytes?.Enqueue(value);
    }
};
var romCalls = new Dictionary<int, long>();
var schedulerTrace = new List<(long Cycle, int Entry, int Context, int Descriptor, byte Process, int ResultPc)>();
var postedEvents = new List<(long Cycle, int Site, byte Source, byte Destination, int Record, ushort Signal, string Bytes)>();
var receivedChannelBytes = new ConcurrentQueue<(long Cycle, int Pc, int Channel, byte Value)>();
byteChannels.ByteReceived += (channel, value) =>
{
    if (traceEvents)
    {
        receivedChannelBytes.Enqueue((clock.Cycles, cpu.PC, channel, value));
    }
};
var watchedPcCounts = watchedPcs.ToDictionary(pc => pc, _ => 0L);
var watchedPcFirstCycles = new Dictionary<int, long>();
var previousPc = -1;
var stopReason = "instruction limit";
string? romStopRegisters = null;
var simRxInjectedByteCount = 0L;
var simRxBurstIndex = 0L;
simRxBursts = [.. simRxBursts.OrderBy(burst => burst.Cycle)];
foreach (var burst in simRxBursts)
{
    var byteCount = burst.Bytes.Length;
    machine.Diagnostics.ScheduleSimReceiveAt(
        burst.Cycle,
        burst.Bytes,
        () =>
        {
            Interlocked.Add(ref simRxInjectedByteCount, byteCount);
            Interlocked.Increment(ref simRxBurstIndex);
        });
}
var logicalElpmReads = 0L;
var logicalElpmMinAddress = int.MaxValue;
var logicalElpmMaxAddress = -1;
var logicalElpmPcs = new Dictionary<int, long>();
var instructionTrace = new List<string>();
var instructionHistory = new Queue<string>();
var instructionTraceActive = false;
var schedulerContext = -1;
var needsExecutionObserver =
    stopPc.HasValue ||
    stopRomCopySource.HasValue ||
    traceStartPc.HasValue ||
    traceHistoryCount > 0 ||
    watchedPcs.Count > 0 ||
    traceEvents ||
    traceScheduler;

if (needsExecutionObserver)
{
    machine.ExecutionObserver = new DelegateExecutionObserver(
        beforeWorkItem: observedMachine =>
        {
            var observedCpu = observedMachine.Cpu;
            if (instructionTraceActive && instructionTrace.Count >= traceInstructionCount)
            {
                return $"completed {traceInstructionCount:n0}-instruction trace";
            }
            if (traceStartPc == observedCpu.PC && ++traceStartSeen == traceStartHit)
            {
                instructionTraceActive = true;
            }
            if (instructionTraceActive)
            {
                instructionTrace.Add(FormatInstructionTrace(observedCpu));
            }
            if (traceHistoryCount > 0)
            {
                instructionHistory.Enqueue(FormatInstructionTrace(observedCpu));
                if (instructionHistory.Count > traceHistoryCount)
                {
                    instructionHistory.Dequeue();
                }
            }

            if (observedMachine.ExecutedInstructions > 0 &&
                observedCpu.PC == stopPc && ++stopPcSeen == stopPcHit)
            {
                return $"reached requested stop PC word 0x{observedCpu.PC:x6}, " +
                    $"from word 0x{previousPc:x6}";
            }
            if (stopRomCopySource.HasValue &&
                observedCpu.PC is 0x3f013a or 0x3f0142 or
                    0x3f0152 or 0x3f0156 or 0x3f015a &&
                (observedCpu.Data[20] |
                    observedCpu.Data[21] << 8 |
                    observedCpu.Data[22] << 16) == stopRomCopySource.Value)
            {
                return $"reached ROM block copy from 0x{stopRomCopySource:x6} " +
                    $"at word 0x{observedCpu.PC:x6}, from word 0x{previousPc:x6}";
            }
            if (watchedPcCounts.TryGetValue(observedCpu.PC, out var watchedCount))
            {
                watchedPcCounts[observedCpu.PC] = watchedCount + 1;
                watchedPcFirstCycles.TryAdd(observedCpu.PC, clock.Cycles);
            }
            if (traceEvents && observedCpu.PC == 0x001838 &&
                (tracedEventDestinations.Count == 0 ||
                 tracedEventDestinations.Contains(observedCpu.Data[20])))
            {
                var arguments = observedCpu.Data[16] |
                    observedCpu.Data[17] << 8 |
                    observedCpu.Data[18] << 16;
                var physicalArguments = observedCpu.TranslateDataAddress(arguments);
                if (physicalArguments >= 0 &&
                    physicalArguments + 2 < observedCpu.Data.Length)
                {
                    var record = observedCpu.ReadData(physicalArguments) |
                        observedCpu.ReadData(physicalArguments + 1) << 8;
                    if (record >= 0 && record + 1 < observedCpu.Data.Length)
                    {
                        var eventBytes = Convert.ToHexString(observedCpu.Data.AsSpan(
                            record,
                            Math.Min(eventTraceByteCount, observedCpu.Data.Length - record)));
                        postedEvents.Add((
                            clock.Cycles,
                            previousPc,
                            observedCpu.ReadData(0xf606),
                            observedCpu.Data[20],
                            record,
                            (ushort)(observedCpu.ReadData(record) |
                                observedCpu.ReadData(record + 1) << 8),
                            eventBytes));
                    }
                }
            }
            if (observedCpu.PC >= AsicRom.StartWord)
            {
                romCalls[observedCpu.PC] =
                    romCalls.GetValueOrDefault(observedCpu.PC) + 1;
                schedulerContext = observedCpu.PC is >= 0x3f015c and <= 0x3f0164
                    ? observedCpu.GetUint16(30) | observedCpu.Data[0x5b] << 16
                    : -1;
                return null;
            }

            previousPc = observedCpu.PC;
            var opcode = observedCpu.GetProgWord(observedCpu.PC);
            if ((observedCpu.Data[0x5b] & 0x80) != 0 &&
                (opcode == 0x95d8 || (opcode & 0xfe0f) is 0x9006 or 0x9007))
            {
                var address = ((observedCpu.Data[0x5b] << 16) |
                    observedCpu.GetUint16(30)) & 0x7fffff;
                logicalElpmReads++;
                logicalElpmMinAddress = Math.Min(logicalElpmMinAddress, address);
                logicalElpmMaxAddress = Math.Max(logicalElpmMaxAddress, address);
                logicalElpmPcs[observedCpu.PC] =
                    logicalElpmPcs.GetValueOrDefault(observedCpu.PC) + 1;
            }
            return null;
        },
        afterRomDispatch: (observedMachine, entry, _) =>
        {
            if (!traceScheduler || schedulerContext < 0)
            {
                return;
            }
            var observedCpu = observedMachine.Cpu;
            var schedulerInfo = AsicRom.GetSchedulerContexts(observedCpu)
                .FirstOrDefault(context => context.Address == schedulerContext);
            var schedulerDescriptor = schedulerInfo?.DescriptorAddress ?? 0;
            var schedulerProcess = schedulerInfo?.Process ?? (byte)0;
            schedulerTrace.Add((
                clock.Cycles,
                entry,
                schedulerContext,
                schedulerDescriptor,
                schedulerProcess,
                observedCpu.PC));
        });
}

PcmWaveFileWriter? audioWaveWriter = null;
AsicTonePcmRenderer? audioRenderer = null;
var audioToneStates = new ConcurrentQueue<AsicToneState>();
Action<AsicToneState>? audioToneStateChanged = null;
var audioRenderBuffer = new short[2_048];
var audioToneStateCount = 0L;
if (audioWavPath is not null)
{
    try
    {
        var fullAudioWavPath = Path.GetFullPath(audioWavPath);
        var directory = Path.GetDirectoryName(fullAudioWavPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        audioRenderer = new(startCycle: machine.Cycles);
        audioWaveWriter = new(fullAudioWavPath, audioRenderer.SampleRate);
        audioToneStateChanged = state =>
        {
            audioToneStates.Enqueue(state);
            Interlocked.Increment(ref audioToneStateCount);
        };
        machine.ToneGenerator.StateChanged += audioToneStateChanged;
    }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
    {
        audioWaveWriter?.Dispose();
        PrintErrorLine($"Could not create audio WAV capture: {error.Message}");
        return 2;
    }
}
using var audioWaveLifetime = audioWaveWriter;

while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    var remaining = instructionLimit - machine.ExecutedInstructions;
    var workItemBudget = (int)Math.Min(16_384, remaining);
    if (liveGsmNetwork)
    {
        machine.RunInteractiveWorkItems(workItemBudget);
    }
    else
    {
        machine.RunWorkItems(workItemBudget);
    }
    if (audioRenderer is not null && audioWaveWriter is not null)
    {
        DrainToneAudio(
            audioRenderer,
            audioWaveWriter,
            audioToneStates,
            machine.Cycles,
            audioRenderBuffer);
    }
}
if (audioToneStateChanged is not null)
{
    machine.ToneGenerator.StateChanged -= audioToneStateChanged;
}
audioWaveWriter?.Dispose();
machine.Diagnostics.FlushHostEvents();
var executed = machine.ExecutedInstructions;
stopReason = machine.StopReason ?? "instruction limit";
if (machine.StopReason?.StartsWith("unimplemented ROM service", StringComparison.Ordinal) == true)
{
    romStopRegisters = machine.Diagnostics.InspectAvr(
        stoppedCpu => Convert.ToHexString(stoppedCpu.Data.AsSpan(16, 16)));
}

if (channel0TransmitPath is not null && channel0TransmitBytes is not null)
{
    try
    {
        File.WriteAllBytes(channel0TransmitPath, [.. channel0TransmitBytes]);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not write channel-0 capture: {error.Message}");
        return 2;
    }
}
if (channel1TransmitPath is not null && channel1TransmitBytes is not null)
{
    try
    {
        File.WriteAllBytes(channel1TransmitPath, [.. channel1TransmitBytes]);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not write channel-1 capture: {error.Message}");
        return 2;
    }
}
if (lcdCapturePath is not null && lcdCapture is not null)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(lcdCapturePath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    File.WriteAllBytes(lcdCapturePath, [.. lcdCapture]);
}
if (lcdFramePath is not null)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(lcdFramePath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    File.WriteAllBytes(lcdFramePath, display.Framebuffer.ToArray());
}
if (gdfsCapturePath is not null)
{
    try
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(gdfsCapturePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(
            gdfsCapturePath,
            machine.CaptureGdfsImage());
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not write GDFS capture: {error.Message}");
        return 2;
    }
}
if (modemDebugPath is not null && modem is not null)
{
    try
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modemDebugPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(modemDebugPath, [.. modemDebugBytes]);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not write modem debug capture: {error.Message}");
        return 2;
    }
}
if (modemUart1TransmitPath is not null && modem is not null)
{
    try
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modemUart1TransmitPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(modemUart1TransmitPath, [.. modemTransmitBytes]);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        PrintErrorLine($"Could not write modem UART1 capture: {error.Message}");
        return 2;
    }
}

PrintLine($"Image:        {Path.GetFullPath(imagePath)} (0x{image.Length:x} bytes)");
if (modem is not null)
{
    PrintLine(
        $"Modem image:  {Path.GetFullPath(modemPath!)} " +
        $"(0x{modem.Image.PayloadLength:x} bytes at 0x{modem.Image.LoadAddress:x8})");
}
foreach (var (gdfsPath, address, length) in loadedGdfs)
{
    PrintLine($"GDFS image:   {Path.GetFullPath(gdfsPath)} (0x{length:x} bytes at 0x{address:x})");
}
if (audioWaveWriter is not null)
{
    PrintLine(
        $"Audio WAV:    {Path.GetFullPath(audioWavPath!)} " +
        $"({audioWaveWriter.SampleCount:n0} samples, {audioToneStateCount:n0} tone writes)");
}
PrintLine($"Instructions: {executed:n0}");
PrintLine($"Cycles:       {clock.Cycles:n0}");
if (machine.GdfsSectorHeaderFastForwardCount != 0)
{
    PrintLine(
        "GDFS sector fast-forward: " +
        $"runs={machine.GdfsSectorHeaderFastForwardCount:n0}, " +
        $"instructions={machine.GdfsSectorHeaderFastForwardedInstructions:n0}");
}
PrintLine($"PC/SP:        0x{cpu.PC:x6} / 0x{cpu.SP:x4}");
PrintLine($"Stopped:      {stopReason}");
if (stopPc.HasValue || stopRomCopySource.HasValue)
{
    PrintLine($"Registers:    r16..r31={Convert.ToHexString(cpu.Data[16..32])}");
    PrintLine($"Extended:     RAMPD={cpu.Data[0x58]:x2} RAMPX={cpu.Data[0x59]:x2} RAMPY={cpu.Data[0x5a]:x2} RAMPZ={cpu.Data[0x5b]:x2} EIND={cpu.Data[0x5c]:x2} SREG={cpu.SREG:x2}");
    var logicalY = cpu.GetUint16(28) | cpu.Data[0x5a] << 16;
    var logicalZ = cpu.GetUint16(30) | cpu.Data[0x5b] << 16;
    PrintLine(
        $"Effective:    Y=0x{logicalY:x6}->0x{cpu.TranslateDataAddress(logicalY):x6} " +
        $"Z=0x{logicalZ:x6}->0x{cpu.TranslateDataAddress(logicalZ):x6}");
}
if (romStopRegisters is not null)
{
    PrintLine($"ROM ABI:      r16..r31={romStopRegisters}");
}
PrintLine("ROM entries:");
foreach (var pair in romCalls.OrderBy(x => x.Key))
{
    PrintLine($"  0x{pair.Key:x6}: {pair.Value:n0}");
}
PrintLine($"Byte TX:      ch0={byteChannels.GetTransmitCount(0):n0}, ch1={byteChannels.GetTransmitCount(1):n0}");
PrintLine($"Byte RX:      ch0={byteChannels.GetReceiveCount(0):n0}, ch1={byteChannels.GetReceiveCount(1):n0} (ch1 queued={byteChannels.GetReceiveQueueLength(1):n0}, window={byteChannels.GetReceiveWindowRemaining(1):n0})");
PrintLine($"Link RX IRQ:  {byteChannels.GetReceiveInterruptCount(1):n0} (logical {AsicInterruptController.LinkReceiveSource:x2})");
if (modem is not null)
{
    var modemState = modem.StopReason ?? (modem.IsSleeping
        ? "firmware sleep (IRQ-wakeable)"
        : "running at AVR stop boundary");
    var modemTransmitPreview = Convert.ToHexString([.. modemTransmitBytes.Take(256)]);
    var modemTransmitSuffix = modemTransmitBytes.Count > 256 ? "..." : "";
    var modemReceivePreview = Convert.ToHexString([.. modemReceiveBytes.Take(256)]);
    var modemReadPreview = Convert.ToHexString([.. modemReadBytes.Take(256)]);
    string modemDebugPreview = new([.. modemDebugBytes.Take(256).Select(value =>
        value is >= 0x20 and <= 0x7e ? (char)value : '.')]);
    PrintLine(
        $"ARM modem:    instructions={modem.Instructions:n0}, cycles={modem.Cycles:n0}, " +
        $"pc=0x{modem.CurrentInstructionAddress:x8}, cpsr=0x{modem.Cpu.CpsrValue:x8}");
    PrintLine(
        $"ARM state:    {modemState}; ROM calls={modem.RomServices.CallCount:n0}, " +
        $"timer IRQs={modem.Bus.TimerInterruptCount:n0}");
    PrintLine(
        $"Modem UART1:  tx={modem.Bus.Uart1TransmitCount:n0} byte(s) " +
        $"{modemTransmitPreview}{modemTransmitSuffix}; " +
        $"tx-fifo={modem.Bus.Uart1TransmitQueuedCount:n0}, " +
        $"rx-fifo={modem.Bus.Uart1ReceivedCount:n0}, " +
        $"pre-enable-dropped={modem.Bus.Uart1DroppedReceiveCount:n0}");
    PrintLine(
        $"Modem UART RX: accepted={modemReceivePreview}, read={modemReadPreview}");
    PrintLine(
        $"Modem debug:  tx={modem.Bus.Uart2TransmitCount:n0} byte(s) " +
        $"{modemDebugPreview}{(modemDebugBytes.Count > 256 ? "..." : "")}");
    PrintLine(
        $"Modem link:   ASIC->ARM={machine.AsicToModemByteCount:n0}, " +
        $"ARM->ASIC={machine.ModemToAsicByteCount:n0}");
}
PrintLine($"I2C:          steps={i2c.CompletedSteps:n0}, transactions={i2c.CompletedTransactions:n0}, last-address=0x{i2c.LastAddress:x2}");
PrintLine($"Power ports:  reads={powerPortController.ReadCount:n0}, writes={powerPortController.WriteCount:n0}, unsupported-reads={powerPortController.UnsupportedReadCount:n0}, rejected-writes={powerPortController.RejectedWriteCount:n0}, EXT1={powerPortController.InterruptCount:n0}, key={(powerPortController.PowerPressed ? "pressed" : "released")}, revision=0x{powerPortController.SiliconRevision:x2}, battery-adc=0x{powerPortController.BatteryAdcSample:x2}, adc-control=0x{powerPortController.AdcControl:x2}, mask=0x{powerPortController.InterruptMask:x2}");
var secondaryOutputs = secondaryPortController.OutputState;
PrintLine($"Secondary I2C: reads={secondaryPortController.ReadCount:n0}, writes={secondaryPortController.WriteCount:n0}, opaque={secondaryPortController.OpaqueWriteCount:n0}, unsupported-reads={secondaryPortController.UnsupportedReadCount:n0}, rejected-writes={secondaryPortController.RejectedWriteCount:n0}, identity=0x{secondaryPortController.Profile.Identity:x2}, status=0x{secondaryPortController.Profile.Status:x2}, 40=0x{secondaryOutputs.Register40:x2}, 48=0x{secondaryOutputs.Register48:x2}, 80=0x{secondaryOutputs.Register80:x2}");
var statusIndicators = machine.StatusIndicators.Diagnostics;
PrintLine($"Status LEDs:  network-green={statusIndicators.State.NetworkGreen}, bluetooth-blue={statusIndicators.State.BluetoothBlue}, charging-active={statusIndicators.ChargingActive}, bluetooth-steady={statusIndicators.BluetoothSteadyDueToCharging}, registered={statusIndicators.NetworkRegistered}, MPH=0x{statusIndicators.NativeMphState:x2}, BT records/operation/completion={statusIndicators.BluetoothControllerRecordCount:n0}/{statusIndicators.BluetoothOperationRequestCount:n0}/{statusIndicators.BluetoothOperationCompletionCount:n0}, BT DSP packets/transfers={statusIndicators.BluetoothDspPacketCount:n0}/{statusIndicators.BluetoothDspTransferCount:n0}, last-operation=0x{statusIndicators.LastBluetoothOperationValue:x2}, BT global-state=0x{statusIndicators.BluetoothControllerStates.GlobalState:x2} ({statusIndicators.BluetoothGlobalStateCommandCount:n0}), link-state-commands={statusIndicators.BluetoothLinkStateCommandCount:n0}, observed-link-mask=0x{statusIndicators.BluetoothControllerStates.ObservedLinkSlotMask:x2}");
PrintLine($"I2C1/LCD:     steps={i2c1.CompletedSteps:n0}, transactions={i2c1.CompletedTransactions:n0}, last-address=0x{i2c1.LastAddress:x2}, display-writes={display.TransactionCount:n0}, pixels={display.PixelWriteCount:n0}, DMA={i2c1.DmaTransferCount:n0}/{i2c1.DmaByteCount:n0}B rejected={i2c1.RejectedDmaCount:n0}");
if (lastLcdScanRow.Cycle != 0)
{
    var schedulerProcess = AsicRom.GetSchedulerContexts(cpu)
        .FirstOrDefault(context =>
            context.Address == lastLcdScanRow.SchedulerRecord ||
            context.DescriptorAddress == lastLcdScanRow.SchedulerRecord)?.Process ??
        AsicRom.GetProcessDescriptors(cpu)
            .FirstOrDefault(descriptor =>
                descriptor.Address == lastLcdScanRow.SchedulerRecord)?.Process;
    var process = schedulerProcess is byte value ? $"0x{value:x2}" : "unknown";
    PrintLine(
        $"LCD last row: cycle={lastLcdScanRow.Cycle:n0}, pc-word=0x{lastLcdScanRow.Pc:x6}, " +
        $"scheduler-record=0x{lastLcdScanRow.SchedulerRecord:x6}, process={process}, " +
        $"DMA-cycle={lastLcdScanRow.DmaCycle:n0}, DMA-pc-word=0x{lastLcdScanRow.DmaPc:x6}, " +
        $"source=0x{lastLcdScanRow.DmaSource:x6}, length={lastLcdScanRow.DmaLength:n0}, " +
        $"payload={lastLcdScanRow.PayloadLength:n0}B");
}
if (liveFramebuffer is not null)
{
    PrintLine($"LCD live:     {liveFramebuffer.FilePath} ({liveFramebuffer.PublishedFrameCount:n0} atomic frame(s))");
}
PrintLine($"Time gen:     commands={timeGenerator.CompletedCommands:n0}/{timeGenerator.StartedCommands:n0}, descriptors={timeGenerator.ProgrammedDescriptorCount:n0}, frame-rollovers={timeGenerator.FrameRolloverCount:n0}, enabled={timeGenerator.FrameRolloverEnabled}, status=0x{timeGenerator.CommandStatus:x2}");
PrintLine($"RF frontend:  transactions={rfFrontend.CommittedTransactionCount:n0}");
if (rfSignalSource is not null)
{
    PrintLine(
        $"RF source:    ARFCN={rfSignalSource.Arfcn}, raw=0x{rfSignalSource.RawSample:x4}, " +
        $"decoded={rfSignalSource.DecodedSampleCount:n0}, matched={rfSignalSource.MatchedSampleCount:n0}");
}
if (gsmCell is not null)
{
    PrintLine(
        $"GSM cell:     ARFCN={gsmCell.Arfcn}, BSIC={gsmCell.Bsic}, " +
        $"FCH={gsmCell.FchSuccessCount:n0}/{gsmCell.FchAttemptCount:n0}, " +
        $"SCH={gsmCell.SchDecodeCount:n0}, CC={gsmCell.ControlChannelDecodeCount:n0}, " +
        $"RACH={gsmCell.RandomAccessRequestCount:n0}, IA={gsmCell.ImmediateAssignmentCount:n0}, " +
        $"UL={gsmCell.DedicatedUplinkCount:n0}, SABM={gsmCell.SabmCount:n0}, " +
        $"UA={gsmCell.UaCount:n0}, LU accept={gsmCell.LocationUpdatingAcceptCount:n0}, " +
        $"RR={gsmCell.ReceiveReadyCount:n0}, release={gsmCell.ChannelReleaseCount:n0}, " +
        $"registered={gsmCell.Registered}, CM service={gsmCell.CmServiceRequestCount:n0}, " +
        $"cipher command={gsmCell.CipheringModeCommandCount:n0}, " +
        $"cipher complete={gsmCell.CipheringModeCompleteCount:n0}, " +
        $"MM active={gsmCell.MmConnectionActive}, I={gsmCell.DedicatedUplinkInformationCount:n0}, " +
        $"CP data={gsmCell.SmsCpDataCount:n0}, RP data={gsmCell.SmsRpDataCount:n0}, " +
        $"RP SMMA={gsmCell.SmsRpSmmaCount:n0}, " +
        $"CP ack={gsmCell.SmsCpAckCount:n0}/{gsmCell.MobileSmsCpAckCount:n0}, " +
        $"RP ack/error={gsmCell.SmsRpAckCount:n0}/{gsmCell.SmsRpErrorCount:n0}, " +
        $"outgoing SMS/call={gsmCell.OutgoingSmsRequestCount:n0}/" +
        $"{gsmCell.OutgoingCallRequestCount:n0}, progress=" +
        $"{gsmCell.CallProceedingCount:n0}/{gsmCell.CallAlertingCount:n0}/" +
        $"{gsmCell.CallConnectCount:n0}, " +
        $"paging={gsmCell.PagingRequestCount:n0}/{gsmCell.PagingResponseCount:n0}, " +
        $"incoming SMS={gsmCell.MobileTerminatedSmsCount:n0}/" +
        $"{gsmCell.DeliveredIncomingSmsCount:n0}, " +
        $"incoming call={gsmCell.MobileTerminatedCallSetupCount:n0}/" +
        $"{gsmCell.MobileTerminatedCallConfirmedCount:n0}/" +
        $"{gsmCell.MobileTerminatedCallConnectCount:n0}/" +
        $"{gsmCell.MobileTerminatedTrafficAssignmentCount:n0}/" +
        $"{gsmCell.MobileTerminatedTrafficAssignmentDeliveredCount:n0}/" +
        $"{gsmCell.MobileTerminatedTrafficAssignmentCompleteCount:n0}, " +
        $"MT CP/RP ack={gsmCell.MobileTerminatedSmsCpAckCount:n0}/" +
        $"{gsmCell.MobileTerminatedSmsRpAckCount:n0}, " +
        $"mobile release={gsmCell.MobileDedicatedReleaseCount:n0}");
    foreach (var request in outgoingGsmRequests)
    {
        PrintLine(
            $"GSM outgoing:  {request.Kind} " +
            $"to={(request.International ? "+" : "")}{request.NormalizedDestination} " +
            $"text={SanitizeGsmSummaryText(request.SmsText)}");
    }
    PrintLine(
        "GSM dec state: " + string.Join(
            ' ',
            Enumerable.Range(0, byte.MaxValue + 1)
                .Select(state => (
                    State: state,
                    Count: gsmCell.GetControlChannelStateCount((byte)state)))
                .Where(item => item.Count != 0)
                .Select(item => $"{item.State:x2}={item.Count:n0}")));
}
if (machine.LiveGsm is { } liveGsmSummary)
{
    var status = liveGsmSummary.GetStatus(machine);
    PrintLine(
        $"Live GSM:     registered={status.Registered}, RSSI={status.RssiLevel}/5, " +
        $"paging={status.PagingRequests:n0}/{status.PagingResponses:n0}, " +
        $"incoming SMS={status.DeliveredIncomingSms:n0}, " +
        $"incoming call setup/confirm/connect=" +
        $"{status.IncomingCallSetups:n0}/{status.IncomingCallConfirmed:n0}/" +
        $"{status.IncomingCallConnects:n0}");
}

static void DrainToneAudio(
    AsicTonePcmRenderer renderer,
    PcmWaveFileWriter writer,
    ConcurrentQueue<AsicToneState> pendingStates,
    long targetCycle,
    Span<short> buffer)
{
    while (pendingStates.TryDequeue(out var state))
    {
        AsicTonePcmRenderResult result;
        do
        {
            result = renderer.RenderTo(state, buffer);
            writer.WriteSamples(buffer[..result.SamplesWritten]);
        }
        while (!result.ReachedTarget);
    }

    AsicTonePcmRenderResult tail;
    do
    {
        tail = renderer.RenderTo(targetCycle, buffer);
        writer.WriteSamples(buffer[..tail.SamplesWritten]);
    }
    while (!tail.ReachedTarget);
}

static string SanitizeGsmSummaryText(string value) =>
    '"' + new string(value.Select(character =>
        character is >= ' ' and <= '~' ? character : '�').ToArray()) + '"';
PrintLine($"ADC:          completions={adc.CompletionCount:n0}, result-reads={adc.ResultReadCount:n0}, queued={Enumerable.Range(0, AsicAdc.SelectorCount).Sum(selector => adc.GetPendingResultCount((byte)selector)):n0}, IRQs={interruptController.GetRaisedCount(AsicInterruptController.AdcDoneSource):n0}");
var fchDetector = machine.FchDetector;
PrintLine($"FCH detector: starts={fchDetector.StartedCount:n0}, completions={fchDetector.CompletedCount:n0}, successes={fchDetector.SuccessfulCount:n0}");
var channelDecoder = machine.ChannelDecoder;
PrintLine($"Channel dec:  starts={channelDecoder.StartedCount:n0}, completions={channelDecoder.CompletedCount:n0}, successes={channelDecoder.SuccessfulCount:n0}, status=0x{machine.Cpu.Data[AsicChannelDecoder.StatusAddress]:x2}, IRQs={interruptController.GetRaisedCount(AsicInterruptController.ChannelDecoderDoneSource):n0}");
var equalizer = machine.Equalizer;
PrintLine($"Equalizer:    starts={equalizer.StartedCount:n0}, phase1={equalizer.Phase1StartedCount:n0}, phase2={equalizer.Phase2StartedCount:n0}, completions={equalizer.CompletedCount:n0}, invalid=0x{machine.Cpu.Data[AsicEqualizer.InvalidResultAddress]:x2}, IRQs={interruptController.GetRaisedCount(AsicInterruptController.EqualizerDoneSource):n0}");
var transferController = machine.TransferController;
PrintLine($"Transfer ctl: starts={transferController.StartedCount:n0}, completions={transferController.CompletedCount:n0}, rejected={transferController.RejectedCount:n0}, selector=0x{transferController.LastSelector:x2}");
PrintLine($"RTC:          ticks={rtc.TickCount:n0}, compares={rtc.SecondCompareCount:n0}, rollovers={rtc.SecondRolloverCount:n0}, calendar={cpu.Data[AsicRtc.CenturyAddress]:x2}{cpu.Data[AsicRtc.YearAddress]:x2}-{cpu.Data[AsicRtc.MonthAddress]:x2}-{cpu.Data[AsicRtc.DayAddress]:x2} {cpu.Data[AsicRtc.HourAddress]:x2}:{cpu.Data[AsicRtc.MinuteAddress]:x2}:{cpu.Data[AsicRtc.SecondAddress]:x2}");
PrintLine($"HP requests:  proven-MMIO={highPriorityRequests.RequestCount:n0}");
PrintLine($"PH command:   commands={phCommandController.CompletedCommands:n0}/{phCommandController.StartedCommands:n0}, status=0x{phCommandController.Status:x2}");
var commandPreview = Convert.ToHexString([.. commandPortBytes.Take(256)]);
var commandSuffix = commandPortBytes.Count > 256 ? "..." : "";
PrintLine($"ASIC command: {commandPort.WrittenByteCount:n0} byte(s) {commandPreview}{commandSuffix}");
PrintLine($"ASIC graphics: packets={commandPort.CompletedPacketCount:n0}, rejected={commandPort.RejectedPacketCount:n0}, raster-ops={commandPort.RasterOperationCount:n0}, RAM-pixels={commandPort.PixelWriteCount:n0}");
var uploadedFirmware = AsicRom.GetUploadedFirmware(cpu);
if (!uploadedFirmware.IsEmpty)
{
    var zeroLengthDestination = AsicRom.GetUploadedFirmwareZeroLengthDestination(cpu);
    var finalTransfer = zeroLengthDestination is int destination
        ? $"zero-length destination 0x{destination:x}"
        : "no zero-length transfer";
    PrintLine($"ASIC upload:  {AsicRom.GetUploadedFirmwareByteCount(cpu):n0} bytes written, private image length 0x{uploadedFirmware.Length:x}, {finalTransfer}");
    var logicalElpmRange = logicalElpmReads > 0
        ? $"0x{logicalElpmMinAddress:x6}..0x{logicalElpmMaxAddress:x6}"
        : "none";
    PrintLine($"Flash window: data-reads={flashMemory.ReadCount:n0}, writes={flashMemory.WriteCount:n0}, logical-ELPM={logicalElpmReads:n0} ({logicalElpmRange})");
    if (logicalElpmPcs.Count > 0)
    {
        PrintLine("Logical ELPM sites:");
        foreach (var pair in logicalElpmPcs.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key))
        {
            PrintLine($"  pc=0x{pair.Key:x6} count={pair.Value:n0}");
        }
    }
    if (asicFirmwarePath is not null)
    {
        try
        {
            File.WriteAllBytes(asicFirmwarePath, uploadedFirmware.ToArray());
            PrintLine($"ASIC image:   {Path.GetFullPath(asicFirmwarePath)}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PrintErrorLine($"Could not write ASIC firmware image: {error.Message}");
            return 2;
        }
    }
}
if (channel0TransmitPath is not null)
{
    PrintLine($"Channel 0 TX: {Path.GetFullPath(channel0TransmitPath)} ({channel0TransmitBytes!.Count:n0} bytes captured)");
}
if (channel1TransmitPath is not null)
{
    PrintLine($"Channel 1 TX: {Path.GetFullPath(channel1TransmitPath)} ({channel1TransmitBytes!.Count:n0} bytes captured)");
}
if (gdfsCapturePath is not null)
{
    PrintLine($"GDFS capture: {Path.GetFullPath(gdfsCapturePath)} (0x{GdfsImage.RawLength:x} bytes)");
}
if (modemDebugPath is not null && modem is not null)
{
    PrintLine($"Modem debug:  {Path.GetFullPath(modemDebugPath)} ({modemDebugBytes.Count:n0} bytes captured)");
}
if (modemUart1TransmitPath is not null && modem is not null)
{
    PrintLine($"Modem UART1:  {Path.GetFullPath(modemUart1TransmitPath)} ({modemTransmitBytes.Count:n0} bytes captured)");
}
var learnedRoutes = string.Join(' ', interruptRouter.Routes
    .OrderBy(pair => pair.Key)
    .Select(pair => $"{pair.Key:x2}->process {pair.Value.ProcessDestination:x2}"));
PrintLine($"IRQ routes:   {interruptRouter.Routes.Count:n0} learned from firmware ({learnedRoutes})");
PrintLine($"Link TX IRQ:  {byteChannels.GetTransmitInterruptCount(1):n0} (logical {AsicInterruptController.LinkTransmitSource:x2})");
var simTransmitPreview = Convert.ToHexString([.. simTransmitBytes.Take(256)]);
var simTransmitSuffix = simTransmitBytes.Count > 256 ? "..." : "";
PrintLine($"SIM I/O:      rx={simInterface.ReceivedByteCount:n0}/{simRxInjectedByteCount:n0} injected byte(s) consumed, {simInterface.QueuedByteCount:n0} queued; tx={simInterface.TransmittedByteCount:n0} byte(s) {simTransmitPreview}{simTransmitSuffix}");
PrintLine($"SIM bursts:   {simRxBurstIndex:n0}/{simRxBursts.Count:n0} injected; TX character time={simInterface.TransmitCompletionCycles:n0} cycles");
if (simCard is not null)
{
    PrintLine($"SIM card:     swSIM-derived profile IMSI={simCard.Imsi}; activations={simCard.ActivationCount:n0}, commands={simCard.CommandCount:n0}");
    foreach (var command in simCommands)
    {
        PrintLine($"  APDU {Convert.ToHexString(command)}");
    }
}
PrintLine($"SED timer:    {sedTimer.TickCount:n0} tick(s) (period {AsicSedTimer.TickCycles:n0} cycles, enabled={sedTimer.Enabled})");
if (printSchedulerContexts)
{
    PrintLine("Scheduler contexts:");
    foreach (var context in AsicRom.GetSchedulerContexts(cpu))
    {
        PrintLine($"  process=0x{context.Process:x2} context=0x{context.Address:x6} descriptor=0x{context.DescriptorAddress:x6} entry=0x{context.TaskEntry:x6} saved=0x{context.SavedPc:x6} sp=0x{context.SavedStackPointer:x4} y=0x{context.SoftwareStackPointer:x6} banks={context.RampD:x2}/{context.RampX:x2}/{context.RampZ:x2}/{context.Eind:x2} sreg=0x{context.Sreg:x2} state=0x{context.State:x2} runnable={context.Runnable} registers={Convert.ToHexString(context.Registers.Span)} hardware-stack=0x{context.HardwareStackStart:x4}:{Convert.ToHexString(context.HardwareStack.Span)}");
    }
}
if (traceScheduler)
{
    PrintLine("Scheduler trace:");
    foreach (var item in schedulerTrace)
    {
        PrintLine($"  cycle={item.Cycle,10} entry=0x{item.Entry:x6} context=0x{item.Context:x6} descriptor=0x{item.Descriptor:x6} process=0x{item.Process:x2} result-pc=0x{item.ResultPc:x6}");
    }
}
if (printProcessDescriptors)
{
    PrintLine("Process descriptors:");
    foreach (var descriptor in AsicRom.GetProcessDescriptors(cpu))
    {
        PrintLine($"  process=0x{descriptor.Process:x2} descriptor=0x{descriptor.Address:x6} entry=0x{descriptor.TaskEntry:x6} state=0x{descriptor.State:x2}");
    }
}
foreach (var (start, length) in dataRanges)
{
    PrintLine($"Data 0x{start:x6}..0x{start + length:x6}:");
    for (var offset = 0; offset < length; offset += 16)
    {
        var count = Math.Min(16, length - offset);
        PrintLine($"  {start + offset:x6}: {Convert.ToHexString(cpu.Data.AsSpan(start + offset, count))}");
    }
}
if (traceEvents)
{
    PrintLine("Posted events:");
    foreach (var posted in postedEvents)
    {
        PrintLine($"  cycle={posted.Cycle,10} site=0x{posted.Site:x6} source=0x{posted.Source:x2} destination=0x{posted.Destination:x2} signal=0x{posted.Signal:x4} record=0x{posted.Record:x6} bytes={posted.Bytes}");
    }
    PrintLine("Received channel bytes:");
    foreach (var received in receivedChannelBytes)
    {
        PrintLine($"  cycle={received.Cycle,10} pc=0x{received.Pc:x6} channel={received.Channel} value=0x{received.Value:x2}");
    }
}
if (traceGsmUplink)
{
    PrintLine("Dedicated GSM downlink decoder starts:");
    foreach (var state in gsmDownlinkTraceStates)
    {
        PrintLine(
            $"  cycle={state.Cycle,10} state={state.FirmwareState}");
    }
    var droppedDownlink = Math.Max(
        0, gsmDownlinkTraceStateCount - MaximumGsmUplinkTraceFrames);
    if (droppedDownlink != 0)
    {
        PrintLine($"  {droppedDownlink:n0} further start(s) omitted");
    }
    PrintLine("Dedicated GSM uplink frames:");
    foreach (var frame in gsmUplinkTraceFrames)
    {
        PrintLine($"  cycle={frame.Cycle,10} state={frame.FirmwareState} bytes={frame.Bytes}");
    }
    var dropped = Math.Max(0, gsmUplinkTraceFrameCount - MaximumGsmUplinkTraceFrames);
    if (dropped != 0)
    {
        PrintLine($"  {dropped:n0} further frame(s) omitted");
    }
}
if (watchedPcCounts.Count > 0)
{
    PrintLine("Watched PCs:");
    foreach (var pair in watchedPcCounts.OrderBy(pair => pair.Key))
    {
        var first = watchedPcFirstCycles.TryGetValue(pair.Key, out var cycle) ? cycle.ToString(CultureInfo.InvariantCulture) : "never";
        PrintLine($"  pc=0x{pair.Key:x6} count={pair.Value:n0} first-cycle={first}");
    }
}
if (pcHotspotCount > 0)
{
    PrintLine("PC hotspots:");
    foreach (var hotspot in machine.Diagnostics
                 .SnapshotExecutionHotspots()
                 .Take(pcHotspotCount))
    {
        PrintLine(
            $"  pc=0x{hotspot.ProgramCounter:x6} " +
            $"count={hotspot.Executions:n0} " +
            $"backedges={hotspot.BackwardBranches:n0}");
    }
}
if (idleFastForwardDiagnostics)
{
    var idleDiagnostics = machine.Diagnostics
        .SnapshotIdleFastForwardDiagnostics();
    var loop = idleDiagnostics.LoopStartWord is int loopStart
        ? $"0x{loopStart:x6}"
        : "unresolved";
    PrintLine(
        "Idle fast-forward: " +
        $"loop={loop} candidates={idleDiagnostics.CandidateCount:n0} " +
        $"successes={idleDiagnostics.SuccessCount:n0} " +
        $"skipped={idleDiagnostics.FastForwardedInstructions:n0} " +
        $"last={idleDiagnostics.LastBlockReason}");
    foreach (var block in idleDiagnostics.BlockCounts
                 .OrderByDescending(block => block.Count)
                 .ThenBy(block => block.Reason))
    {
        PrintLine($"  blocked={block.Reason} count={block.Count:n0}");
    }
}
if (instructionTrace.Count > 0)
{
    PrintLine("Instruction trace:");
    foreach (var line in instructionTrace)
    {
        PrintLine(line);
    }
}
if (instructionHistory.Count > 0)
{
    PrintLine("Instruction history:");
    foreach (var line in instructionHistory)
    {
        PrintLine(line);
    }
}
if (!watchedDataWrites.IsEmpty || watchedDataAddresses.Count > 0)
{
    PrintLine("Watched data writes:");
    foreach (var write in watchedDataWrites)
    {
        PrintLine($"  cycle={write.Cycle,10} pc=0x{write.Pc:x6} address=0x{write.Address:x6} {write.OldValue:x2}->{write.NewValue:x2} mask={write.Mask:x2}");
    }
}
if (!watchedDataReads.IsEmpty || watchedReadAddresses.Count > 0)
{
    PrintLine("Watched data reads:");
    foreach (var read in watchedDataReads)
    {
        PrintLine($"  cycle={read.Cycle,10} pc=0x{read.Pc:x6} address=0x{read.Address:x6} value={read.Value:x2}");
    }
}

return 0;

static void HandleInteractiveKeypadCommand(
    MiaMachine machine,
    string hostCommand)
{
    var parts = hostCommand.Split(
        ' ',
        StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries);
    if (parts.Length is 4 or 5 && parts[0] == "key" &&
        byte.TryParse(
            parts[2],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var scanMask) &&
        byte.TryParse(
            parts[3],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var rowMask) &&
        AsicKeypad.IsValidContact(scanMask, rowMask) &&
        (parts.Length == 4 || byte.TryParse(
            parts[4],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out _)) &&
        parts[1] is "down" or "up")
    {
        byte? secondaryScanMask = parts.Length == 5
            ? byte.Parse(
                parts[4],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture)
            : null;
        var transition = machine.SetKey(
            scanMask,
            rowMask,
            secondaryScanMask,
            parts[1] == "down");
        PrintLine(
            $"Keypad input: cycle={machine.Cycles} key {parts[1]} " +
            $"scan=0x{scanMask:x2} " +
            (secondaryScanMask is byte secondary
                ? $"secondary=0x{secondary:x2} "
                : string.Empty) +
            $"row=0x{rowMask:x2} " +
            DescribeInteractiveTransition(transition));
        return;
    }
    if (parts.Length == 2 && parts[0] == "power" &&
        parts[1] is "down" or "up")
    {
        var transition = machine.SetPowerKey(parts[1] == "down");
        PrintLine(
            $"Keypad input: cycle={machine.Cycles} power {parts[1]} " +
            DescribeInteractiveTransition(transition));
    }
}

static bool TryParseScheduledKeyEvent(
    string text,
    out (long Cycle, byte ScanMask, byte RowMask, byte? SecondaryScanMask, bool Pressed) keyEvent)
{
    keyEvent = default;
    var parts = text.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length is not (4 or 5) ||
        !long.TryParse(parts[0], out var cycle) || cycle < 0 ||
        parts[1] is not ("down" or "up") ||
        !byte.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var scanMask) ||
        !byte.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rowMask) ||
        !AsicKeypad.IsValidContact(scanMask, rowMask))
    {
        return false;
    }

    byte? secondaryScanMask = null;
    if (parts.Length == 5)
    {
        if (!byte.TryParse(parts[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var secondary) ||
            !AsicKeypad.IsValidContact(secondary, rowMask) ||
            secondary == scanMask || scanMask == 0x0f || secondary == 0x0f)
        {
            return false;
        }
        secondaryScanMask = secondary;
    }

    keyEvent = (cycle, scanMask, rowMask, secondaryScanMask, parts[1] == "down");
    return true;
}

static bool TryParseScheduledPowerKeyEvent(
    string text,
    out (long Cycle, bool Pressed) keyEvent)
{
    keyEvent = default;
    var parts = text.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 2 ||
        !long.TryParse(parts[0], out var cycle) || cycle < 0 ||
        parts[1] is not ("down" or "up"))
    {
        return false;
    }

    keyEvent = (cycle, parts[1] == "down");
    return true;
}

static string DescribeInteractiveTransition(InteractiveKeypadTransition transition) =>
    transition switch
    {
        InteractiveKeypadTransition.Applied => "applied",
        InteractiveKeypadTransition.Deferred =>
            "deferred until firmware MMIO observation",
        InteractiveKeypadTransition.Unchanged => "unchanged",
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };

static string FormatInstructionTrace(Cpu cpu)
{
    var opcode = cpu.PC >= 0 && cpu.PC < cpu.ProgWords ? cpu.GetProgWord(cpu.PC) : 0;
    return
        $"  cycle={cpu.Cycles,10} pc=0x{cpu.PC:x6} sp=0x{cpu.SP:x4} op={opcode:x4} " +
        $"r0..r15={Convert.ToHexString(cpu.Data.AsSpan(0, 16))} " +
        $"r16..r31={Convert.ToHexString(cpu.Data.AsSpan(16, 16))} " +
        $"RAMPD={cpu.Data[0x58]:x2} RAMPX={cpu.Data[0x59]:x2} RAMPY={cpu.Data[0x5a]:x2} " +
        $"RAMPZ={cpu.Data[0x5b]:x2} EIND={cpu.Data[0x5c]:x2} SREG={cpu.Data[0x5f]:x2}";
}
