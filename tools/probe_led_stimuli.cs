#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using Arm7Core;
using Mia.Emulator;

var useLiveGsm = !args.Contains("--no-live-gsm", StringComparer.Ordinal);
var externalPowerConnected = args.Contains(
    "--external-power",
    StringComparer.Ordinal);
var connectExternalPowerAfterStandby = args.Contains(
    "--connect-external-after-standby",
    StringComparer.Ordinal);
if (externalPowerConnected && connectExternalPowerAfterStandby)
{
    throw new ArgumentException(
        "Choose initial or post-standby external power, not both.");
}
byte powerPortSiliconRevision = args.FirstOrDefault(
    arg => arg.StartsWith("--power-silicon-revision=", StringComparison.Ordinal))
    is { } revisionArgument
    ? Convert.ToByte(
        revisionArgument["--power-silicon-revision=".Length..],
        16)
    : MiaPowerPortController.LegacySiliconRevision;
var requestedOperation = args.FirstOrDefault(
    arg => arg.StartsWith("--operation=", StringComparison.Ordinal))?
    ["--operation=".Length..] ?? "on";
long? requestedStandbyInstructions = args.FirstOrDefault(
    arg => arg.StartsWith("--standby-instructions=", StringComparison.Ordinal))
    is { } standbyArgument
    ? Convert.ToInt64(standbyArgument["--standby-instructions=".Length..])
    : null;
if (requestedStandbyInstructions <= 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--standby-instructions must be positive.");
}
var operationIndex = requestedOperation switch
{
    "off" => 0,
    "automatic" => 1,
    "on" => 2,
    _ => throw new ArgumentOutOfRangeException(
        nameof(args),
        $"Unknown Bluetooth operation mode '{requestedOperation}'."),
};
var operationReturn = args.FirstOrDefault(
    arg => arg.StartsWith("--operation-return=", StringComparison.Ordinal))?
    ["--operation-return=".Length..] ?? "center";
byte[][] injectedDspRecords = args
    .Where(arg => arg.StartsWith("--dsp-record=", StringComparison.Ordinal))
    .Select(arg => Convert.FromHexString(arg["--dsp-record=".Length..]))
    .ToArray();
byte[][] pairingDspRecords = args
    .Where(arg => arg.StartsWith("--pairing-dsp-record=", StringComparison.Ordinal))
    .Select(arg => Convert.FromHexString(arg["--pairing-dsp-record=".Length..]))
    .ToArray();
if (injectedDspRecords.Any(record =>
        record.Length != ArmModemBluetoothPeripheral.ControllerRecordLength))
{
    throw new ArgumentException("--dsp-record must contain exactly 17 bytes.");
}
if (pairingDspRecords.Any(record =>
        record.Length != ArmModemBluetoothPeripheral.ControllerRecordLength))
{
    throw new ArgumentException(
        "--pairing-dsp-record must contain exactly 17 bytes.");
}
bool injectDspRecordSweep = args.Contains("--dsp-record-sweep", StringComparer.Ordinal);
bool injectDspRecordFlaggedSweep = args.Contains(
    "--dsp-record-sweep-flagged",
    StringComparer.Ordinal);
bool injectDspRecordFullSweep = args.Contains(
    "--dsp-record-full-sweep",
    StringComparer.Ordinal);
bool appendDspRecordFullSweep = args.Contains(
    "--append-dsp-record-full-sweep",
    StringComparer.Ordinal);
bool waitForDiscovery = args.Contains("--wait-discovery", StringComparer.Ordinal);
bool stopDiscovery = args.Contains("--stop-discovery", StringComparer.Ordinal);
bool stopDiscoveryWithYes = args.Contains(
    "--stop-discovery-with-yes",
    StringComparer.Ordinal);
bool stopDiscoveryWithClear = args.Contains(
    "--stop-discovery-with-clear",
    StringComparer.Ordinal);
bool acknowledgeStoppedDiscovery = args.Contains(
    "--ack-stop-discovery",
    StringComparer.Ordinal);
bool selectDiscoveredPeer = args.Contains(
    "--select-discovered-peer",
    StringComparer.Ordinal);
bool addDiscoveredPeer = args.Contains(
    "--add-discovered-peer",
    StringComparer.Ordinal);
string[] postPairingKeys = args
    .Where(arg => arg.StartsWith("--post-pairing-key=", StringComparison.Ordinal))
    .Select(arg => arg["--post-pairing-key=".Length..])
    .ToArray();
string? pairingPasskey = args.FirstOrDefault(
    arg => arg.StartsWith("--pairing-passkey=", StringComparison.Ordinal))?
    ["--pairing-passkey=".Length..];
if (pairingPasskey is not null &&
    (pairingPasskey.Length == 0 || pairingPasskey.Any(character => !char.IsDigit(character))))
{
    throw new ArgumentException("--pairing-passkey must contain one or more digits.");
}
bool nudgeDiscoveredPeer = args.Contains(
    "--nudge-discovered-peer",
    StringComparer.Ordinal);
bool sweepDiscoveryKeys = args.Contains(
    "--sweep-discovery-keys",
    StringComparer.Ordinal);
bool openPairedDevices = args.Contains(
    "--open-paired-devices",
    StringComparer.Ordinal);
bool addPairedDevice = args.Contains(
    "--add-paired-device",
    StringComparer.Ordinal);
bool startPairing = args.Contains(
    "--start-pairing",
    StringComparer.Ordinal);
if ((injectDspRecordSweep || injectDspRecordFlaggedSweep ||
     injectDspRecordFullSweep) &&
    injectedDspRecords.Length != 0)
{
    throw new ArgumentException(
        "Use either --dsp-record or a DSP record sweep option.");
}
ushort? tracedAction = args.FirstOrDefault(
    arg => arg.StartsWith("--trace-action=", StringComparison.Ordinal)) is { } actionArgument
    ? Convert.ToUInt16(actionArgument["--trace-action=".Length..], 16)
    : null;
string? tracedActionStage = args.FirstOrDefault(
    arg => arg.StartsWith("--trace-action-stage=", StringComparison.Ordinal))?
    ["--trace-action-stage=".Length..];
ushort? tracedSignal = args.FirstOrDefault(
    arg => arg.StartsWith("--trace-signal=", StringComparison.Ordinal)) is { } signalArgument
    ? Convert.ToUInt16(signalArgument["--trace-signal=".Length..], 16)
    : null;
ushort? tracedSendSignal = args.FirstOrDefault(
    arg => arg.StartsWith("--trace-send=", StringComparison.Ordinal)) is { } sendArgument
    ? Convert.ToUInt16(sendArgument["--trace-send=".Length..], 16)
    : null;
bool traceDspWire = args.Contains("--trace-dsp-wire", StringComparer.Ordinal);
bool injectPeerObjectPushPut = args.Contains(
    "--inject-peer-object-push-put",
    StringComparer.Ordinal);
bool injectPeerObjectPushConnect = args.Contains(
    "--inject-peer-object-push-connect",
    StringComparer.Ordinal) ||
    injectPeerObjectPushPut;
bool injectPeerObjectPushSabm =
    injectPeerObjectPushConnect ||
    args.Contains(
        "--inject-peer-object-push-sabm",
        StringComparer.Ordinal);
byte? watchedSwbpEndpoint = args.FirstOrDefault(
    arg => arg.StartsWith("--watch-swbp-endpoint=", StringComparison.Ordinal))
    is { } watchedSwbpEndpointArgument
        ? Convert.ToByte(
            watchedSwbpEndpointArgument["--watch-swbp-endpoint=".Length..],
            16)
        : null;
bool acknowledgeFixedTransferImmediately = args.Contains(
    "--ack-fixed-immediate",
    StringComparer.Ordinal);
bool acknowledgeC3efCompletion = args.Contains(
    "--ack-c3ef-completion",
    StringComparer.Ordinal);
bool setC3efState3 = args.Contains(
    "--set-c3ef-state3",
    StringComparer.Ordinal);
bool forcePostC3efWorkPending = args.Contains(
    "--force-post-c3ef-work-pending",
    StringComparer.Ordinal);
bool forcePostC3efSchedulerPending = args.Contains(
    "--force-post-c3ef-scheduler-pending",
    StringComparer.Ordinal);
bool forceC3ddDispatch = args.Contains(
    "--force-c3dd-dispatch",
    StringComparer.Ordinal);
bool suppressIdleDspTransfers = args.Contains(
    "--no-idle-dsp-transfers",
    StringComparer.Ordinal);
bool emulateRemoteName = args.Contains(
    "--emulate-remote-name",
    StringComparer.Ordinal);
bool traceAvrD2ff = args.Contains(
    "--trace-avr-d2ff",
    StringComparer.Ordinal);
bool traceAvrD2fa = args.Contains(
    "--trace-avr-d2fa",
    StringComparer.Ordinal);
bool traceAvrD2fc = args.Contains(
    "--trace-avr-d2fc",
    StringComparer.Ordinal);
ushort? tracedAvrSignalArgument = args.FirstOrDefault(
    arg => arg.StartsWith("--trace-avr-signal=", StringComparison.Ordinal))
    is { } avrSignalArgument
        ? Convert.ToUInt16(
            avrSignalArgument["--trace-avr-signal=".Length..],
            16)
        : null;
if (new[] { traceAvrD2ff, traceAvrD2fa, traceAvrD2fc }.Count(enabled => enabled) +
    (tracedAvrSignalArgument is null ? 0 : 1) > 1)
{
    throw new ArgumentException(
        "Trace only one AVR Bluetooth signal per run.");
}
ushort? tracedAvrBluetoothSignal = tracedAvrSignalArgument ?? (traceAvrD2fc
    ? (ushort)0xd2fc
    : traceAvrD2fa
        ? (ushort)0xd2fa
        : traceAvrD2ff
            ? (ushort)0xd2ff
            : null);
bool injectD2fbAfterD2fa = args.Contains(
    "--inject-d2fb-after-d2fa",
    StringComparer.Ordinal);
bool injectD2fbAfterD2ff = args.Contains(
    "--inject-d2fb-after-d2ff",
    StringComparer.Ordinal);
bool injectDspTransfersAfterC3ef = args.Contains(
    "--inject-dsp-transfer-after-c3ef",
    StringComparer.Ordinal);
bool dspTransfersInjectedAfterC3ef = false;
long responseInstructions = args.FirstOrDefault(
    arg => arg.StartsWith("--response-instructions=", StringComparison.Ordinal)) is { } responseArgument
    ? Convert.ToInt64(responseArgument["--response-instructions=".Length..])
    : 40_000_000;
if (responseInstructions <= 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--response-instructions must be positive.");
}
long? pairingPostInstructions = args.FirstOrDefault(
    arg => arg.StartsWith(
        "--pairing-post-instructions=",
        StringComparison.Ordinal)) is { } pairingPostArgument
    ? Convert.ToInt64(
        pairingPostArgument["--pairing-post-instructions=".Length..])
    : null;
if (pairingPostInstructions <= 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--pairing-post-instructions must be positive.");
}
long postPairingKeyInstructions = args.FirstOrDefault(
    arg => arg.StartsWith(
        "--post-pairing-key-instructions=",
        StringComparison.Ordinal)) is { } postPairingKeyArgument
    ? Convert.ToInt64(
        postPairingKeyArgument["--post-pairing-key-instructions=".Length..])
    : 12_000_000;
if (postPairingKeyInstructions <= 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--post-pairing-key-instructions must be positive.");
}
long postPairingWaitInstructions = args.FirstOrDefault(
    arg => arg.StartsWith(
        "--post-pairing-wait-instructions=",
        StringComparison.Ordinal)) is { } postPairingWaitArgument
    ? Convert.ToInt64(
        postPairingWaitArgument["--post-pairing-wait-instructions=".Length..])
    : 0;
if (postPairingWaitInstructions < 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--post-pairing-wait-instructions cannot be negative.");
}
long responseInjectionDelayInstructions = args.FirstOrDefault(
    arg => arg.StartsWith("--response-injection-delay-instructions=", StringComparison.Ordinal)) is { } delayArgument
    ? Convert.ToInt64(delayArgument["--response-injection-delay-instructions=".Length..])
    : 0;
if (responseInjectionDelayInstructions < 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--response-injection-delay-instructions cannot be negative.");
}
var injectedControllerFrames = args
    .Where(arg => arg.StartsWith("--controller-frame=", StringComparison.Ordinal))
    .Select(arg => ParseControllerFrame(arg["--controller-frame=".Length..]))
    .ToArray();
var injectedAsicFrames = args
    .Where(arg => arg.StartsWith("--asic-frame=", StringComparison.Ordinal))
    .Select(arg => ParseControllerFrame(arg["--asic-frame=".Length..]))
    .ToArray();
var injectedDspTransfers = args
    .Where(arg => arg.StartsWith("--dsp-transfer=", StringComparison.Ordinal))
    .Select(arg => ParseDspTransfer(arg["--dsp-transfer=".Length..]))
    .ToArray();
var overriddenDspIndexes = args
    .Where(arg => arg.StartsWith("--dsp-index=", StringComparison.Ordinal))
    .Select(arg => ParseDspIndex(arg["--dsp-index=".Length..]))
    .ToArray();
byte[] injectedDspStatuses = args
    .Where(arg => arg.StartsWith("--dsp-status=", StringComparison.Ordinal))
    .Select(arg => Convert.ToByte(arg["--dsp-status=".Length..], 16))
    .ToArray();
var scheduledDspStatuses = new Queue<(byte State, byte Status)>(
    args.Where(arg => arg.StartsWith("--dsp-status-on-state=", StringComparison.Ordinal))
        .Select(arg => ParseDspStatusOnState(
            arg["--dsp-status-on-state=".Length..])));
var scheduledDspStatusEvents = new List<string>();
long scheduledDspStatusSpacingInstructions = args.FirstOrDefault(
    arg => arg.StartsWith("--dsp-status-spacing-instructions=", StringComparison.Ordinal)) is { } statusSpacingArgument
    ? Convert.ToInt64(
        statusSpacingArgument["--dsp-status-spacing-instructions=".Length..])
    : 500_000;
if (scheduledDspStatusSpacingInstructions < 0)
{
    throw new ArgumentOutOfRangeException(
        nameof(args),
        "--dsp-status-spacing-instructions cannot be negative.");
}
long nextScheduledDspStatusInstruction = 0;
var injectedDspSharedBytes = args
    .Where(arg => arg.StartsWith("--dsp-shared-byte=", StringComparison.Ordinal))
    .Select(arg => ParseDspSharedByte(arg["--dsp-shared-byte=".Length..]))
    .ToArray();
var frameDirectory = args.FirstOrDefault(
    arg => arg.StartsWith("--frames=", StringComparison.Ordinal))?
    ["--frames=".Length..];
string[]? avrDumpArgument = args.FirstOrDefault(
    arg => arg.StartsWith("--avr-dump=", StringComparison.Ordinal))?
    ["--avr-dump=".Length..]
    .Split(',', StringSplitOptions.TrimEntries);
(int Address, int Length)? avrDump =
    avrDumpArgument is { Length: 2 } &&
    int.TryParse(
        avrDumpArgument[0],
        System.Globalization.NumberStyles.HexNumber,
        null,
        out int avrDumpAddress) &&
    int.TryParse(avrDumpArgument[1], out int avrDumpLength)
        ? (avrDumpAddress, avrDumpLength)
        : null;
if (frameDirectory is not null)
{
    Directory.CreateDirectory(frameDirectory);
}
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
    liveGsm: useLiveGsm ? new MiaLiveGsmOptions() : null,
    externalPowerConnectedInitially: externalPowerConnected,
    powerPortSiliconRevision: powerPortSiliconRevision);
if (injectPeerObjectPushSabm)
{
    // Keep the diagnostic injector and the production peer state machine
    // mutually exclusive. Both operate at the DSP transfer boundary.
    machine.BluetoothPeripheral!.EmulateIncomingObjectPush = false;
}
var avrD2ffTrace = new List<string>();
if (watchedSwbpEndpoint is { } swbpEndpoint)
{
    machine.ExecutionObserver = new AvrSignalAndSwbpEndpointObserver(
        avrD2ffTrace,
        tracedAvrBluetoothSignal,
        swbpEndpoint);
}
else if (tracedAvrBluetoothSignal is { } avrBluetoothSignal)
{
    machine.ExecutionObserver = new AvrBluetoothSignalObserver(
        avrD2ffTrace,
        avrBluetoothSignal);
}

var stage = "boot";
var action = "boot";
var discoveryGraphicsPackets = new List<string>();
var discoveryGraphicsRejections = new List<string>();
var pendingGraphicsPacket = new List<byte>();
var recentGraphicsBytes = new Queue<string>();
long observedGraphicsRejections = machine.CommandPort.RejectedPacketCount;
string previousGraphicsPacket = "--";
long graphicsPacketStartInstruction = 0;
int graphicsPacketStartPc = 0;
machine.CommandPort.ByteWritten += value =>
{
    long currentRejections = machine.CommandPort.RejectedPacketCount;
    if (currentRejections != observedGraphicsRejections &&
        stage == "bluetooth-discovery-response" &&
        discoveryGraphicsRejections.Count < 4_000)
    {
        discoveryGraphicsRejections.Add(
            $"i={machine.ExecutedInstructions:n0} pc=0x{machine.Cpu.PC:x6} " +
            $"count={currentRejections:n0} delta=" +
            $"{currentRejections - observedGraphicsRejections:n0} " +
            $"previous={previousGraphicsPacket} " +
            $"recent={string.Join(",", recentGraphicsBytes)}");
    }
    observedGraphicsRejections = currentRejections;

    if (stage != "bluetooth-discovery-response")
    {
        pendingGraphicsPacket.Clear();
        recentGraphicsBytes.Clear();
        return;
    }

    string byteDescription =
        $"{machine.ExecutedInstructions:n0}:0x{machine.Cpu.PC:x6}:{value:x2}";
    recentGraphicsBytes.Enqueue(byteDescription);
    while (recentGraphicsBytes.Count > 24)
    {
        recentGraphicsBytes.Dequeue();
    }

    if (pendingGraphicsPacket.Count == 0)
    {
        graphicsPacketStartInstruction = machine.ExecutedInstructions;
        graphicsPacketStartPc = machine.Cpu.PC;
    }
    pendingGraphicsPacket.Add(value);
    int packetLength = GraphicsPacketLength(pendingGraphicsPacket[0]);
    if (packetLength == 0 || pendingGraphicsPacket.Count == packetLength)
    {
        previousGraphicsPacket =
            $"start-i={graphicsPacketStartInstruction:n0} " +
            $"start-pc=0x{graphicsPacketStartPc:x6} " +
            $"bytes={Convert.ToHexString(pendingGraphicsPacket.ToArray())}";
        if (discoveryGraphicsPackets.Count < 8_000)
        {
            discoveryGraphicsPackets.Add(previousGraphicsPacket);
        }
        pendingGraphicsPacket.Clear();
    }
};
const int DiscoveryFramebufferAddress = 0x10ac94;
const int DiscoveryFramebufferLength = S4595Display.Width * S4595Display.Height;
var discoveryFramebufferWriters = new Dictionary<
    int,
    (long Count, int MinOffset, int MaxOffset, long FirstInstruction, long LastInstruction)>();
for (var address = DiscoveryFramebufferAddress;
     address < DiscoveryFramebufferAddress + DiscoveryFramebufferLength;
     address++)
{
    int framebufferOffset = address - DiscoveryFramebufferAddress;
    var previousHook = machine.Cpu.WriteHooks[address];
    machine.Cpu.WriteHooks[address] = (value, oldValue, hookAddress, mask) =>
    {
        byte effectiveValue = (byte)((oldValue & ~mask) | (value & mask));
        if (stage == "bluetooth-discovery-response" &&
            effectiveValue != oldValue)
        {
            int pc = machine.Cpu.PC;
            long instruction = machine.ExecutedInstructions;
            if (discoveryFramebufferWriters.TryGetValue(pc, out var stats))
            {
                discoveryFramebufferWriters[pc] = (
                    stats.Count + 1,
                    Math.Min(stats.MinOffset, framebufferOffset),
                    Math.Max(stats.MaxOffset, framebufferOffset),
                    stats.FirstInstruction,
                    instruction);
            }
            else
            {
                discoveryFramebufferWriters[pc] = (
                    1,
                    framebufferOffset,
                    framebufferOffset,
                    instruction,
                    instruction);
            }
        }
        return previousHook?.Invoke(value, oldValue, hookAddress, mask) ?? false;
    };
}
var discoveryDisplayTransactions = new List<string>();
byte[]? previousDiscoveryScan = null;
machine.DisplayI2c.TransactionCompleted += (address, payload) =>
{
    if (stage != "bluetooth-discovery-response" ||
        address != S4595Display.WriteAddress ||
        payload.Length < 2 ||
        payload[0] != S4595Display.ScanRowCommand)
    {
        return;
    }

    int changed = payload.Length - 1;
    if (previousDiscoveryScan is not null &&
        previousDiscoveryScan.Length == payload.Length - 1)
    {
        changed = 0;
        for (var index = 1; index < payload.Length; index++)
        {
            if (payload[index] != previousDiscoveryScan[index - 1])
            {
                changed++;
            }
        }
    }
    discoveryDisplayTransactions.Add(
        $"i={machine.ExecutedInstructions:n0} cycle={machine.Cycles:n0} " +
        $"pc=0x{machine.DisplayI2c.LastWritePc:x6} " +
        $"dma-pc=0x{machine.DisplayI2c.LastDmaPc:x6} " +
        $"dma-source=0x{machine.DisplayI2c.LastDmaSource:x6} " +
        $"dma-length={machine.DisplayI2c.LastDmaLength:n0} " +
        $"pixels={payload.Length - 1:n0} changed={changed:n0} " +
        $"hash={Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan(1)))}");
    previousDiscoveryScan = payload[1..];
};
var center = new Contact(0x0f, 0x08);
var yes = new Contact(0x0d, 0x01);
var right = new Contact(0x0e, 0x10, 0x07);
var down = new Contact(0x0e, 0x10, 0x0d);
var left = new Contact(0x0d, 0x10, 0x0b);
var up = new Contact(0x0b, 0x10, 0x07);
var clear = new Contact(0x0f, 0x02);
var no = new Contact(0x0f, 0x01);
var digitContacts = new Dictionary<char, Contact>
{
    ['1'] = new(0x07, 0x01),
    ['2'] = new(0x0b, 0x01),
    ['3'] = new(0x0e, 0x01),
    ['4'] = new(0x07, 0x02),
    ['5'] = new(0x0b, 0x02),
    ['6'] = new(0x0e, 0x02),
    ['7'] = new(0x07, 0x04),
    ['8'] = new(0x0b, 0x04),
    ['9'] = new(0x0e, 0x04),
    ['0'] = new(0x0b, 0x08),
};
var packets = new Dictionary<(string Stage, byte Type, string Payload), long>();
var transfers = new Dictionary<
    (string Stage, byte Control, int Kind, byte Trailer, string Payload), long>();
var outputWrites = new Dictionary<
    (string Stage, byte Register40, byte Register48, byte Register80), long>();
var powerOutputWrites = new Dictionary<
    (string Stage, MiaPowerPortOutputState State), long>();
var opaqueWrites = new Dictionary<(string Stage, byte Register, byte Value), long>();
var opaqueWriteSequence = new List<string>();
long uartTransmitted = 0;
long uartReceived = 0;
var uartTransmitBytes = new Dictionary<string, List<byte>>();
var uartReceiveBytes = new Dictionary<string, List<byte>>();
var bluetoothControllerFrames = new List<string>();
var nativeDspCommands = new Dictionary<(string Stage, byte Type, string Payload), long>();
var nativeDspCommandSequence = new List<string>();
var linkManagerActions = new List<string>();
var oseSignalSends = new List<string>();
var oseSignalReceives = new List<string>();
var reconnectMailboxTrace = new List<string>();
var driverTaskLoopTrace = new List<string>();
string? previousDriverTaskLoopSignature = null;
bool reconnectMailboxTraceArmed = false;
int reconnectSendInstructionTraceRemaining = 0;
var btreqRequestDispatches = new List<string>();
var d2d6SignalSends = new List<string>();
var secondaryTransferHandlers = new List<string>();
var driverC3efContexts = new List<string>();
var c3efStateBranchTrace = new List<string>();
var c3efInstructionTrace = new List<string>();
var c3efInstructionTraceRemaining = 0;
var c3f6ProducerContexts = new List<string>();
bool pendingC3efCompletion = false;
bool c3efReceived = false;
var postC3efTransferPath = new List<string>();
var postC3efWorkInjections = new List<string>();
var forcedC3ddDispatches = new List<string>();
bool c3ddDispatchForced = false;
var driverReceiveGateChanges = new List<string>();
byte? previousDriverReceiveGate = null;
var driverContextWrites = new List<string>();
var driverAssemblyWrites = new List<string>();
var linkManagerContextWrites = new List<string>();
var linkManagerProducerWrites = new List<string>();
var driverPayloadAssembly = new List<string>();
var allocatorGuardWrites = new List<string>();
var c3ddHeaderLifecycle = new List<string>();
var dspIndexedAccesses = new List<string>();
var dspInboundFifoAccesses = new List<string>();
byte selectedDspIndex = 0;
var decodedControllerRecords = new List<string>();
byte[] activeControllerRecord = [];
var bondDatabaseWrites = new List<string>();
var bondDatabaseRequests = new List<string>();
var bondDatabaseInstructionTrace = new List<string>();
uint bondDatabaseResultAddress = 0;
var bluetoothE1Calls = new List<string>();
var bluetoothKeyDerivationCalls = new List<string>();
var signalReceiveTrace = new List<string>();
var signalReceiveTraceRemaining = 0;
var c40cConsumerMatches = new List<string>();
var bluetoothStateTableSearches = new List<string>();
var actionInstructionTrace = new List<string>();
var actionInstructionTraceRemaining = 0;
var dspWireEvents = new List<string>();
var pairingDspWireEvents = new List<string>();
var incomingRfcommDiagnosticEvents = new List<string>();
var outboundL2capProbe = new L2capOutboundProbe(
    incomingRfcommDiagnosticEvents);
bool peerObjectPushPnInjected = false;
bool peerObjectPushSabmInjected = false;
bool peerObjectPushConnectInjected = false;
bool peerObjectPushPutQueued = false;
var peerObjectPushPutFragments = new Queue<(byte Kind, byte[] Payload)>();
var reconnectDspTransportTrace = new List<string>();
bool reconnectDspCounterTraceArmed = false;
long remoteNameAcknowledgementInstruction = long.MaxValue;
long remoteNameRecordInstruction = long.MaxValue;
long pairingDspRecordInstruction = long.MaxValue;
int pairingDspRecordIndex = 0;
long d2fbAfterD2ffInstruction = long.MaxValue;
bool remoteNameRecordQueued = false;
bool d2fbInjected = false;
var bluetoothFrame = new NativeLinkFrameDecoder();
var modemFrame = new AsicLinkFrameDecoder();
var asicReceiveFrame = new AsicLinkFrameDecoder();
var modemToAsicFrames = new List<string>();
var frameIndex = 0;
InteractiveKeypadContact? deferredContact = null;
machine.InteractiveInput.DeferredReleaseApplied += contact =>
{
    if (contact == deferredContact)
    {
        deferredContact = null;
    }
};

var modemBus = machine.Modem?.Bus ??
    throw new InvalidOperationException("The ARM modem did not start.");
foreach (var index in overriddenDspIndexes)
{
    modemBus.SetDspIndexedRegister(index.Address, index.Value);
}
modemBus.ExternalRamWritten += write =>
{
    const uint driverContext = 0x01400c40;
    const uint driverContextLength = 0x60;
    const uint driverAssembly = 0x01400c80;
    const uint driverAssemblyLength = 0x30;
    bool isReconnectTransportStage =
        stage is "bluetooth-post-pairing-34-center" or
            "bluetooth-post-pairing-36-center";
    bool writesOutstandingCounter =
        write.Address < driverContext + 0x36 &&
        write.Address + write.Size > driverContext + 0x34;
    if (isReconnectTransportStage &&
        writesOutstandingCounter &&
        write.Value != 0)
    {
        reconnectDspCounterTraceArmed = true;
    }
    if (isReconnectTransportStage &&
        (reconnectDspCounterTraceArmed || writesOutstandingCounter) &&
        write.Address >= driverContext + 0x30 &&
        write.Address < driverContext + 0x36 &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        reconnectDspTransportTrace.Add(
            $"cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
            $"context-write address=0x{write.Address:x8} size={write.Size} " +
            $"value=0x{write.Value:x8} {DescribeBluetoothOutput(modemBus)}");
    }
    if (isReconnectTransportStage &&
        reconnectDspCounterTraceArmed &&
        write.Address >= 0x014215ec &&
        write.Address < 0x0142197c &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        uint slotOffset = write.Address - 0x014215ec;
        reconnectDspTransportTrace.Add(
            $"cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
            $"slot-write slot={slotOffset / 0x13} field=0x{slotOffset % 0x13:x2} " +
            $"size={write.Size} value=0x{write.Value:x8} " +
            DescribeBluetoothOutput(modemBus));
    }
    if (write.Address >= 0x01421010 &&
        write.Address < 0x01421070 &&
        bondDatabaseWrites.Count < 1_000)
    {
        bondDatabaseWrites.Add(
            $"stage={stage} cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
            $"address=0x{write.Address:x8} size={write.Size} value=0x{write.Value:x8}");
    }
    if (write.Address >= driverAssembly &&
        write.Address < driverAssembly + driverAssemblyLength &&
        driverAssemblyWrites.Count < 4_000)
    {
        driverAssemblyWrites.Add(
            $"stage={stage} cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
            $"address=0x{write.Address:x8} size={write.Size} value=0x{write.Value:x8}");
    }
    if (write.Address >= 0x014210e0 &&
        write.Address < 0x01421600 &&
        linkManagerContextWrites.Count < 4_000)
    {
        linkManagerContextWrites.Add(
            $"stage={stage} cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
            $"address=0x{write.Address:x8} size={write.Size} value=0x{write.Value:x8}");
    }
    // 010bba22 constructs C3F6 from these producer fields.  Keep a separate,
    // response-stage-only trace so boot-time zeroing cannot hide the writes
    // that make a controller event eligible for the native link path.
    if (stage == "bluetooth-discovery-response" &&
        write.Address >= 0x014215d0 &&
        write.Address < 0x01421620 &&
        linkManagerProducerWrites.Count < 1_000)
    {
        linkManagerProducerWrites.Add(
            $"cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
            $"address=0x{write.Address:x8} size={write.Size} value=0x{write.Value:x8}");
    }
    if (write.Address < driverContext ||
        write.Address >= driverContext + driverContextLength ||
        driverContextWrites.Count >= 4_000)
    {
        return;
    }
    driverContextWrites.Add(
        $"stage={stage} cycle={write.Cycle:n0} pc=0x{write.ProgramCounter:x8} " +
        $"address=0x{write.Address:x8} size={write.Size} value=0x{write.Value:x8}");
};
modemBus.MmioAccessed += access =>
{
    if (stage != "bluetooth-discovery-response" &&
        !stage.StartsWith("bluetooth-post-pairing-", StringComparison.Ordinal))
    {
        return;
    }

    if (traceDspWire && access.Address == 0x00800800 &&
        access.Size == 1 && dspWireEvents.Count < 4_000)
    {
        dspWireEvents.Add(
            $"cycle={access.Cycle:n0} pc=0x{access.Pc:x8} " +
            $"{(access.IsWrite ? "status-write" : "status-read")}=" +
            $"0x{access.Value:x2}");
    }
    if (reconnectDspCounterTraceArmed &&
        (stage is "bluetooth-post-pairing-34-center" or
            "bluetooth-post-pairing-36-center") &&
        access.Address == 0x00800800 &&
        access.Size == 1 &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        reconnectDspTransportTrace.Add(
            $"cycle={access.Cycle:n0} pc=0x{access.Pc:x8} " +
            $"{(access.IsWrite ? "status-write" : "status-read")}=" +
            $"0x{access.Value:x2} irq=0x{modemBus.DspInterruptStatus:x2} " +
            DescribeBluetoothOutput(modemBus));
    }
    if (reconnectDspCounterTraceArmed &&
        stage == "bluetooth-post-pairing-36-center" &&
        !access.IsWrite &&
        access.Address == 0x00800810 &&
        access.Size == 1 &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        reconnectDspTransportTrace.Add(
            $"cycle={access.Cycle:n0} pc=0x{access.Pc:x8} " +
            $"fifo-read=0x{access.Value:x2} " +
            DescribeBluetoothOutput(modemBus));
    }

    if (!access.IsWrite && access.Address == 0x00800810 && access.Size == 1 &&
        dspInboundFifoAccesses.Count < 4_000)
    {
        dspInboundFifoAccesses.Add(
            $"cycle={access.Cycle:n0} pc=0x{access.Pc:x8} value=0x{access.Value:x2}");
    }

    if (dspIndexedAccesses.Count >= 4_000)
    {
        return;
    }

    if (access.Address == 0x00800808 && access.Size == 1)
    {
        if (access.IsWrite)
        {
            selectedDspIndex = (byte)access.Value;
            dspIndexedAccesses.Add(
                $"cycle={access.Cycle:n0} pc=0x{access.Pc:x8} " +
                $"index=0x{selectedDspIndex:x2} select");
        }
        else
        {
            dspIndexedAccesses.Add(
                $"cycle={access.Cycle:n0} pc=0x{access.Pc:x8} " +
                $"index=0x{selectedDspIndex:x2} " +
                $"value=0x{(byte)access.Value:x2}");
            selectedDspIndex++;
        }
    }
};
machine.BluetoothPeripheral!.DspCommandQueued += command =>
{
    var key = (stage, command.Type, Convert.ToHexString(command.Payload));
    nativeDspCommands[key] = nativeDspCommands.GetValueOrDefault(key) + 1;
    nativeDspCommandSequence.Add(
        $"stage={stage} cycle={modemBus.Cycles:n0} type=0x{command.Type:x2} " +
        $"payload={Convert.ToHexString(command.Payload)}");
    if (emulateRemoteName && command.Type == 0x48)
    {
        remoteNameAcknowledgementInstruction =
            machine.ExecutedInstructions + 500_000;
    }
};
modemBus.DspPacketTransmitted += packet =>
{
    var key = (stage, packet.Type, Convert.ToHexString(packet.Payload));
    packets[key] = packets.GetValueOrDefault(key) + 1;
    if (traceDspWire &&
        (stage == "bluetooth-discovery-response" ||
         stage.StartsWith("bluetooth-post-pairing-", StringComparison.Ordinal)) &&
        dspWireEvents.Count < 4_000)
    {
        dspWireEvents.Add(
            $"cycle={modemBus.Cycles:n0} packet type={packet.Type:x2} " +
            $"payload={Convert.ToHexString(packet.Payload)}");
    }
    if (reconnectDspCounterTraceArmed &&
        (stage is "bluetooth-post-pairing-34-center" or
            "bluetooth-post-pairing-36-center") &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        reconnectDspTransportTrace.Add(
            $"cycle={modemBus.Cycles:n0} packet type=0x{packet.Type:x2} " +
            $"payload={Convert.ToHexString(packet.Payload)} " +
            DescribeBluetoothOutput(modemBus));
    }
    if (acknowledgeFixedTransferImmediately &&
        packet.Type == ArmModemBluetoothPeripheral.FixedTransferReadyPacketType &&
        packet.Payload.Length == ArmModemBluetoothPeripheral.FixedTransferReadyPacketPayloadLength)
    {
        modemBus.AssertDspStatus(
            ArmModemBluetoothPeripheral.FixedTransferCompletionStatus);
    }
};
modemBus.DspTransferTransmitted += transfer =>
{
    if (transfer.Trailer == 3 &&
        transfer.Kind is
            L2capOutboundProbe.InitialTransferKind or
            L2capOutboundProbe.ContinuationTransferKind)
    {
        outboundL2capProbe.Observe(
            transfer.Kind.Value,
            transfer.Payload,
            stage,
            modemBus.Cycles);
    }
    var key = (
        stage,
        transfer.Control,
        transfer.Kind is { } kind ? kind : -1,
        transfer.Trailer,
        Convert.ToHexString(transfer.Payload));
    transfers[key] = transfers.GetValueOrDefault(key) + 1;
    if (stage == "bluetooth-discovery-response" &&
        transfer.Kind == 7 &&
        pairingDspWireEvents.Count < 1_000)
    {
        pairingDspWireEvents.Add(
            $"cycle={modemBus.Cycles:n0} control={transfer.Control:x2} " +
            $"trailer={transfer.Trailer:x2} " +
            $"payload={Convert.ToHexString(transfer.Payload)}");
    }
    if (traceDspWire &&
        (stage == "bluetooth-discovery-response" ||
         stage.StartsWith("bluetooth-post-pairing-", StringComparison.Ordinal)) &&
        dspWireEvents.Count < 4_000)
    {
        dspWireEvents.Add(
            $"cycle={modemBus.Cycles:n0} transfer control={transfer.Control:x2} " +
            $"kind={(transfer.Kind is { } transferKind ? transferKind.ToString("x2") : "--")} " +
            $"trailer={transfer.Trailer:x2} payload={Convert.ToHexString(transfer.Payload)}");
    }
    if (reconnectDspCounterTraceArmed &&
        (stage is "bluetooth-post-pairing-34-center" or
            "bluetooth-post-pairing-36-center") &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        reconnectDspTransportTrace.Add(
            $"cycle={modemBus.Cycles:n0} transfer control=0x{transfer.Control:x2} " +
            $"kind={(transfer.Kind is { } transferKind ? $"0x{transferKind:x2}" : "--")} " +
            $"trailer=0x{transfer.Trailer:x2} " +
            $"payload={Convert.ToHexString(transfer.Payload)} " +
            DescribeBluetoothOutput(modemBus));
    }
};
modemBus.Uart1ByteTransmitted += value =>
{
    uartTransmitted++;
    if (modemFrame.Push(value) is { } frame)
    {
        modemToAsicFrames.Add(
            $"stage={stage} destination=0x{frame.Destination:x2} " +
            $"source=0x{frame.Source:x2} payload={Convert.ToHexString(frame.Payload)}");
        if (injectD2fbAfterD2ff &&
            frame.Destination == 0x63 &&
            frame.Payload.Length >= 2 &&
            BitConverter.ToUInt16(frame.Payload, 0) == 0xd2ff)
        {
            d2fbAfterD2ffInstruction =
                machine.ExecutedInstructions + 200_000;
        }
    }
    if (stage != "boot")
    {
        uartTransmitBytes.GetOrAdd(action).Add(value);
    }
};
modemBus.Uart1ByteReceived += value =>
{
    uartReceived++;
    if (bluetoothFrame.Push(value) is { } frame && frame.Destination == 0x86)
    {
        bluetoothControllerFrames.Add(
            $"stage={stage} source=0x{frame.Source:x2} " +
            $"payload={Convert.ToHexString(frame.Payload)}");
    }
    if (stage != "boot")
    {
        uartReceiveBytes.GetOrAdd(action).Add(value);
    }
};
machine.ByteChannels.ByteReceived += (channel, value) =>
{
    if (channel == 1 && asicReceiveFrame.Push(value) is { } frame)
    {
        scheduledDspStatusEvents.Add(
            $"cycle={machine.Cycles:n0} avr-read destination=0x{frame.Destination:x2} " +
            $"source=0x{frame.Source:x2} payload={Convert.ToHexString(frame.Payload)}");
    }
};
machine.Modem.InstructionExecuting += modem =>
{
    if (stage == "boot")
    {
        return;
    }

    uint pc = modem.CurrentInstructionAddress;
    const uint oseKernelAddress = 0x0000281c;
    const uint oseCurrentTaskAddress = oseKernelAddress + 0x40;
    const uint bluetoothDriverTask = 0x00008498;
    if (injectPeerObjectPushSabm &&
        stage.StartsWith("bluetooth-post-pairing-", StringComparison.Ordinal) &&
        pc is 0x010c4a28 or 0x010c5960 or 0x010c3268 or 0x010c5258 or
            0x010c356c or 0x01085530 or 0x010c6424 or 0x010c7092 or
            0x01061148 or 0x01061168 or 0x01061188 or 0x010611a8 or
            0x010611c8 or 0x01061270 or 0x01085a98 or 0x01086468 or
            0x010869c4 or
            0x010d87c4 or 0x010866d8 &&
        incomingRfcommDiagnosticEvents.Count < 1_000)
    {
        uint stack = modem.Cpu.GetGpr(13);
        byte[] stackBytes = stack < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(stack, 32)
            : [];
        incomingRfcommDiagnosticEvents.Add(
            $"stage={stage} cycle={modem.Cycles:n0} arm-entry=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            $"sp=0x{stack:x8} lr=0x{modem.Cpu.GetGpr(14):x8} " +
            $"stack={Convert.ToHexString(stackBytes)}");
    }
    if (stage == "bluetooth-post-pairing-36-center" &&
        pc == 0x01003040 &&
        modem.Cpu.GetGpr(1) == 5)
    {
        uint signalPointerAddress = modem.Cpu.GetGpr(0);
        uint signalAddress = ReadArmUInt32(modem.Bus, signalPointerAddress);
        byte[] signal = SnapshotArmMemory(modem.Bus, signalAddress, 16);
        if (signal.Length >= 2 &&
            BitConverter.ToUInt16(signal, 0) == 0xc3ef)
        {
            reconnectMailboxTraceArmed = true;
            reconnectSendInstructionTraceRemaining = 180;
            reconnectMailboxTrace.Add(
                $"send-entry stage={stage} cycle={modem.Cycles:n0} " +
                $"sender-tcb=0x{ReadArmUInt32(modem.Bus, oseCurrentTaskAddress):x8} " +
                $"signal=0x{signalAddress:x8}:{Convert.ToHexString(signal)} " +
                $"kernel={DescribeOseKernel(modem.Bus, oseKernelAddress)} " +
                $"target={DescribeOseTask(modem.Bus, bluetoothDriverTask)}");
        }
    }
    if (reconnectSendInstructionTraceRemaining > 0 &&
        reconnectMailboxTrace.Count < 1_000)
    {
        reconnectMailboxTrace.Add(
            $"send-step cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            $"r4=0x{modem.Cpu.GetGpr(4):x8} r5=0x{modem.Cpu.GetGpr(5):x8} " +
            $"r7=0x{modem.Cpu.GetGpr(7):x8} lr=0x{modem.Cpu.GetGpr(14):x8}");
        reconnectSendInstructionTraceRemaining--;
    }
    uint currentOseTask =
        ReadArmUInt32(modem.Bus, oseCurrentTaskAddress);
    if (reconnectDspCounterTraceArmed &&
        (stage is "bluetooth-post-pairing-34-center" or
            "bluetooth-post-pairing-36-center") &&
        pc is 0x010c0d10 or 0x010c0e44 or 0x010c0e5e or
            0x010c0e84 or 0x010c0ef0 &&
        reconnectDspTransportTrace.Count < 2_000)
    {
        reconnectDspTransportTrace.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            DescribeBluetoothOutput(modem.Bus));
    }
    if (reconnectMailboxTraceArmed &&
        ((pc is 0x01003102 or 0x01003106 &&
          reconnectSendInstructionTraceRemaining > 0) ||
         (currentOseTask == bluetoothDriverTask &&
          pc is 0x01001d6c or 0x01001e7c or 0x01001e94)) &&
        reconnectMailboxTrace.Count < 1_000)
    {
        reconnectMailboxTrace.Add(
            $"mailbox-boundary stage={stage} cycle={modem.Cycles:n0} " +
            $"pc=0x{pc:x8} current-tcb=" +
            $"0x{currentOseTask:x8} " +
            $"filter=0x{modem.Cpu.GetGpr(0):x8} " +
            $"kernel={DescribeOseKernel(modem.Bus, oseKernelAddress)} " +
            $"target={DescribeOseTask(modem.Bus, bluetoothDriverTask)}");
    }
    if (reconnectMailboxTraceArmed &&
        pc is 0x010bdfd2 or 0x010bdfee or 0x010bdff8 or
            0x010be000 &&
        driverTaskLoopTrace.Count < 1_000)
    {
        byte[] context = modem.Bus.SnapshotExternal(0x01400c40, 0x60);
        string signature =
            $"pc={pc:x8};pending=0x{BitConverter.ToUInt32(context, 0x38):x8};" +
            $"count={BitConverter.ToInt16(context, 0x34)};" +
            $"scheduler={context[0x0b]:x2};producer={BitConverter.ToUInt16(context, 0x30)};" +
            $"consumer={BitConverter.ToUInt16(context, 0x32)};state={context[0x56]:x2};" +
            DescribeOseTask(modem.Bus, bluetoothDriverTask);
        if (pc != 0x010bdfd2 || signature != previousDriverTaskLoopSignature)
        {
            driverTaskLoopTrace.Add(
                $"stage={stage} cycle={modem.Cycles:n0} {signature} " +
                $"r0=0x{modem.Cpu.GetGpr(0):x8} sp=0x{modem.Cpu.GetGpr(13):x8}");
            previousDriverTaskLoopSignature = signature;
        }
    }
    if (pc == 0x010abb0e && bluetoothE1Calls.Count < 16)
    {
        uint stack = modem.Cpu.GetGpr(13);
        byte[] stackArguments = SnapshotArmMemory(modem.Bus, stack, 4);
        uint acoAddress = stackArguments.Length == 4
            ? BitConverter.ToUInt32(stackArguments)
            : uint.MaxValue;
        uint keyAddress = modem.Cpu.GetGpr(0);
        uint randomAddress = modem.Cpu.GetGpr(1);
        uint addressAddress = modem.Cpu.GetGpr(2);
        uint sresAddress = modem.Cpu.GetGpr(3);
        bluetoothE1Calls.Add(
            $"cycle={modem.Cycles:n0} key=0x{keyAddress:x8}:" +
            $"{Convert.ToHexString(SnapshotArmMemory(modem.Bus, keyAddress, 16))} " +
            $"random=0x{randomAddress:x8}:" +
            $"{Convert.ToHexString(SnapshotArmMemory(modem.Bus, randomAddress, 16))} " +
            $"address=0x{addressAddress:x8}:" +
            $"{Convert.ToHexString(SnapshotArmMemory(modem.Bus, addressAddress, 6))} " +
            $"sres=0x{sresAddress:x8} aco=0x{acoAddress:x8}");
    }
    if (pc == 0x010abbf2 && bluetoothKeyDerivationCalls.Count < 32)
    {
        uint randomAddress = modem.Cpu.GetGpr(0);
        uint addressAddress = modem.Cpu.GetGpr(1);
        bluetoothKeyDerivationCalls.Add(
            $"cycle={modem.Cycles:n0} E21 " +
            $"random={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, randomAddress, 16))} " +
            $"address={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, addressAddress, 6))}");
    }
    if (pc == 0x010abc0e && bluetoothKeyDerivationCalls.Count < 32)
    {
        uint randomAddress = modem.Cpu.GetGpr(0);
        uint pinAddress = modem.Cpu.GetGpr(1);
        uint pinLengthAddress = modem.Cpu.GetGpr(2);
        byte[] pinLengthBytes =
            SnapshotArmMemory(modem.Bus, pinLengthAddress, 1);
        int pinLength = pinLengthBytes.Length == 1 ? pinLengthBytes[0] : 0;
        bluetoothKeyDerivationCalls.Add(
            $"cycle={modem.Cycles:n0} E22 " +
            $"random={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, randomAddress, 16))} " +
            $"pin={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, pinAddress, pinLength))}");
    }
    if (pc == 0x010abc2c && bluetoothKeyDerivationCalls.Count < 32)
    {
        uint stack = modem.Cpu.GetGpr(13);
        byte[] stackArguments = SnapshotArmMemory(modem.Bus, stack, 4);
        uint outputAddress = stackArguments.Length == 4
            ? BitConverter.ToUInt32(stackArguments)
            : uint.MaxValue;
        uint randomAddress = modem.Cpu.GetGpr(0);
        uint pinAddress = modem.Cpu.GetGpr(1);
        uint pinLengthAddress = modem.Cpu.GetGpr(2);
        byte[] pinLengthBytes =
            SnapshotArmMemory(modem.Bus, pinLengthAddress, 1);
        int pinLength = pinLengthBytes.Length == 1 ? pinLengthBytes[0] : 0;
        uint addressAddress = modem.Cpu.GetGpr(3);
        bluetoothKeyDerivationCalls.Add(
            $"cycle={modem.Cycles:n0} E22-with-address " +
            $"random={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, randomAddress, 16))} " +
            $"pin={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, pinAddress, pinLength))} " +
            $"address={Convert.ToHexString(
                SnapshotArmMemory(modem.Bus, addressAddress, 6))} " +
            $"output=0x{outputAddress:x8}");
    }
    if (pc == 0x010a3c6c)
    {
        uint stack = modem.Cpu.GetGpr(13);
        bondDatabaseResultAddress =
            stack < ArmModemBus.InternalRamSize - 16
                ? BitConverter.ToUInt32(modem.Bus.SnapshotInternal(stack + 12, 4))
                : 0;
    }
    if (pc is >= 0x010a3c6c and < 0x010a3f4c &&
        bondDatabaseInstructionTrace.Count < 2_000)
    {
        byte result = bondDatabaseResultAddress < ArmModemBus.InternalRamSize
            ? modem.Bus.SnapshotInternal(bondDatabaseResultAddress, 1)[0]
            : (byte)0xff;
        bondDatabaseInstructionTrace.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            $"r4=0x{modem.Cpu.GetGpr(4):x8} r7=0x{modem.Cpu.GetGpr(7):x8} " +
            $"sp=0x{modem.Cpu.GetGpr(13):x8} lr=0x{modem.Cpu.GetGpr(14):x8} " +
            $"result-address=0x{bondDatabaseResultAddress:x8} result=0x{result:x2}");
    }
    if (pc == 0x010a6176 && bondDatabaseRequests.Count < 32)
    {
        uint stack = modem.Cpu.GetGpr(13);
        uint signalAddress = stack < ArmModemBus.InternalRamSize - 12
            ? BitConverter.ToUInt32(modem.Bus.SnapshotInternal(stack + 8, 4))
            : 0;
        byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(signalAddress, 32)
            : [];
        byte[] state = modem.Bus.SnapshotExternal(0x01421010, 0x60);
        bondDatabaseRequests.Add(
            $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"signal=0x{signalAddress:x8} bytes={Convert.ToHexString(signal)} " +
            $"state={Convert.ToHexString(state)}");
    }
    if (stage == "bluetooth-discovery-response" &&
        pc == 0x010c1a2a &&
        modem.Cpu.GetGpr(1) is
            0x0000c3f8 or 0x0000c3ff or 0x0000c403 or 0x0000c40c or
            0x0000c40d &&
        bluetoothStateTableSearches.Count < 64)
    {
        bluetoothStateTableSearches.Add(
            $"cycle={modem.Cycles:n0} signal=0x{modem.Cpu.GetGpr(1):x4} " +
            $"state=0x{modem.Cpu.GetGpr(4):x2} " +
            $"entries=0x{modem.Cpu.GetGpr(5):x}..0x{modem.Cpu.GetGpr(6):x}");
    }
    if (stage == "bluetooth-discovery-response" &&
        pairingDspRecordIndex < pairingDspRecords.Length &&
        machine.ExecutedInstructions >= pairingDspRecordInstruction &&
        (modem.Bus.IrqPending & ArmModemBus.DspInterruptBit) == 0)
    {
        byte[] record = pairingDspRecords[pairingDspRecordIndex++];
        machine.QueueBluetoothControllerRecord(record);
        pairingDspRecordInstruction =
            pairingDspRecordIndex < pairingDspRecords.Length
                ? machine.ExecutedInstructions + 5_000_000
                : long.MaxValue;
        scheduledDspStatusEvents.Add(
            $"cycle={modem.Cycles:n0} pairing-record=" +
            Convert.ToHexString(record));
    }
    if (stage == "bluetooth-discovery-response" &&
        injectD2fbAfterD2ff &&
        !d2fbInjected &&
        machine.ExecutedInstructions >= d2fbAfterD2ffInstruction)
    {
        InjectD2fb("after-D2FF");
    }
    if (stage == "bluetooth-discovery-response" &&
        emulateRemoteName &&
        machine.ExecutedInstructions >= remoteNameAcknowledgementInstruction &&
        modem.Bus.SnapshotExternal(0x01400c96, 1)[0] == 1 &&
        (modem.Bus.IrqPending & ArmModemBus.DspInterruptBit) == 0)
    {
        foreach (var index in overriddenDspIndexes)
        {
            modem.Bus.SetDspIndexedRegister(index.Address, index.Value);
        }
        modem.Bus.SetDspIndexedReadAddress(0);
        modem.Bus.AssertDspStatus(0x15);
        remoteNameAcknowledgementInstruction = long.MaxValue;
        scheduledDspStatusEvents.Add(
            $"cycle={modem.Cycles:n0} command=0x48 status=0x15");
    }
    if (stage == "bluetooth-discovery-response" &&
        emulateRemoteName &&
        !remoteNameRecordQueued &&
        machine.ExecutedInstructions >= remoteNameRecordInstruction &&
        (modem.Bus.IrqPending & ArmModemBus.DspInterruptBit) == 0)
    {
        byte[] record = new byte[ArmModemBluetoothPeripheral.ControllerRecordLength];
        record[0] = 0x04;
        record[1] = 0;
        byte[] name = "Mia Peer"u8.ToArray();
        record[2] = checked((byte)name.Length);
        name.CopyTo(record, 3);
        machine.QueueBluetoothControllerRecord(record);
        remoteNameRecordQueued = true;
        scheduledDspStatusEvents.Add(
            $"cycle={modem.Cycles:n0} command=0x48 " +
            $"record={Convert.ToHexString(record)}");
    }
    if (stage == "bluetooth-discovery-response" &&
        scheduledDspStatuses.TryPeek(out var scheduledStatus) &&
        machine.ExecutedInstructions >= nextScheduledDspStatusInstruction &&
        modem.Bus.SnapshotExternal(0x01400c96, 1)[0] == scheduledStatus.State &&
        (modem.Bus.IrqPending & ArmModemBus.DspInterruptBit) == 0)
    {
        foreach (var index in overriddenDspIndexes)
        {
            modem.Bus.SetDspIndexedRegister(index.Address, index.Value);
        }
        modem.Bus.SetDspIndexedReadAddress(0);
        modem.Bus.AssertDspStatus(scheduledStatus.Status);
        scheduledDspStatuses.Dequeue();
        nextScheduledDspStatusInstruction =
            machine.ExecutedInstructions + scheduledDspStatusSpacingInstructions;
        scheduledDspStatusEvents.Add(
            $"cycle={modem.Cycles:n0} state=0x{scheduledStatus.State:x2} " +
            $"status=0x{scheduledStatus.Status:x2}");
    }
    // Diagnostic: these are the first instructions after each statically
    // verified write to BT_Ctrl state+0x16 (external 0x01400c96). Capture
    // Trace the selected consumer, not the preceding table scan, so the
    // active profile table is visible for discovery and pairing records.
    if (stage == "bluetooth-discovery-response" &&
        pc == 0x010c1a4a &&
        modem.Cpu.GetGpr(0) is
            0x0000c3f8 or 0x0000c3ff or 0x0000c403 or 0x0000c40c or
            0x0000c40d or 0x0000c41b &&
        c40cConsumerMatches.Count < 32)
    {
        uint process = modem.Cpu.GetGpr(7);
        uint table = process < ArmModemBus.InternalRamSize - 16
            ? BitConverter.ToUInt32(modem.Bus.SnapshotInternal(process + 12, 4))
            : 0;
        c40cConsumerMatches.Add(
            $"cycle={modem.Cycles:n0} process=0x{process:x8} table=0x{table:x8} " +
            $"index={modem.Cpu.GetGpr(5):x8} count={modem.Cpu.GetGpr(6):x8} " +
            $"r4=0x{modem.Cpu.GetGpr(4):x8}");
        if (tracedSignal == modem.Cpu.GetGpr(0))
        {
            signalReceiveTrace.Clear();
            signalReceiveTraceRemaining = 4_000;
        }
    }
    if (pc is 0x010bb9fc or 0x010bba22 &&
        c3f6ProducerContexts.Count < 1_000)
    {
        uint signalAddress = modem.Cpu.GetGpr(7);
        byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(signalAddress, 32)
            : [];
        c3f6ProducerContexts.Add(
            $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"action=0x{modem.Cpu.GetGpr(0):x4} r4=0x{modem.Cpu.GetGpr(4):x8} " +
            $"r7=0x{signalAddress:x8} signal={Convert.ToHexString(signal)}");
    }
    if (c3efInstructionTraceRemaining > 0)
    {
        c3efInstructionTrace.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            $"lr=0x{modem.Cpu.GetGpr(14):x8}");
        c3efInstructionTraceRemaining--;
    }
    if (stage == "bluetooth-discovery-response" &&
        pc is 0x010c17b8 or 0x01003042 or 0x01003050 or 0x01003052 or
            0x0100305e or 0x01003064 or 0x01003066 &&
        c3ddHeaderLifecycle.Count < 100)
    {
        uint signalAddress = BitConverter.ToUInt32(
            modem.Bus.SnapshotExternal(0x01400c8c, sizeof(uint)));
        byte[] header = signalAddress is >= 12 and < ArmModemBus.InternalRamSize
            ? modem.Bus.SnapshotInternal(signalAddress - 12, 12)
            : [];
        c3ddHeaderLifecycle.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} signal=0x{signalAddress:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r7=0x{modem.Cpu.GetGpr(7):x8} " +
            $"header={Convert.ToHexString(header)}");
    }
    if (stage == "bluetooth-discovery-response" &&
        pc is 0x01001b7a or 0x01001b7c or 0x01001b80 &&
        allocatorGuardWrites.Count < 100)
    {
        uint address = modem.Cpu.GetGpr(0);
        byte[] bytes = address < ArmModemBus.InternalRamSize - 4
            ? modem.Bus.SnapshotInternal(address, 4)
            : [];
        allocatorGuardWrites.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} address=0x{address:x8} " +
            $"r5=0x{modem.Cpu.GetGpr(5):x8} bytes={Convert.ToHexString(bytes)}");
    }
    if (forceC3ddDispatch && !c3ddDispatchForced && pc == 0x010c17b8)
    {
        uint signalAddress = BitConverter.ToUInt32(
            modem.Bus.SnapshotExternal(0x01400c8c, sizeof(uint)));
        if (signalAddress < ArmModemBus.InternalRamSize - 2 &&
            BitConverter.ToUInt16(modem.Bus.SnapshotInternal(signalAddress, 2)) ==
                0xc3dd)
        {
            // Diagnostic-only: expose the native C3DD consumer by taking the
            // sender-ready branch in 010c17b8. The actual DSP condition that
            // raises state+15 is still being recovered.
            modem.Bus.WritePeripheralSharedExternal(0x01400c95, [1]);
            c3ddDispatchForced = true;
            forcedC3ddDispatches.Add(
                $"cycle={modem.Cycles:n0} signal=0x{signalAddress:x8}");
        }
    }
    byte driverReceiveGate = modem.Bus.SnapshotExternal(0x01400c54, 1)[0];
    if (previousDriverReceiveGate != driverReceiveGate &&
        driverReceiveGateChanges.Count < 1_000)
    {
        driverReceiveGateChanges.Add(
            $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"gate=0x{driverReceiveGate:x2} previous=" +
            (previousDriverReceiveGate is { } previous ? $"0x{previous:x2}" : "unset"));
        previousDriverReceiveGate = driverReceiveGate;
    }
    if (pc == 0x010a60b4 && btreqRequestDispatches.Count < 1_000)
    {
        uint signalAddress = modem.Cpu.GetGpr(0);
        byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(signalAddress, 32)
            : [];
        btreqRequestDispatches.Add(
            $"stage={stage} cycle={modem.Cycles:n0} address=0x{signalAddress:x8} " +
            $"bytes={Convert.ToHexString(signal)}");
    }
    else if (stage == "bluetooth-discovery-response" &&
             pc is 0x010c18a4 or 0x010c18ec or 0x010c17b8 &&
             driverPayloadAssembly.Count < 1_000)
    {
        byte[] primary = modem.Bus.SnapshotExternal(0x01400c40, 0x60);
        byte[] assembly = modem.Bus.SnapshotExternal(0x01400c80, 0x30);
        uint signalAddress = BitConverter.ToUInt32(primary, 0x4c);
        byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(signalAddress, 32)
            : [];
        byte[] signalHeader = signalAddress is >= 12 and < ArmModemBus.InternalRamSize
            ? modem.Bus.SnapshotInternal(signalAddress - 12, 12)
            : [];
        ushort signalAllocationLength = signalHeader.Length == 12
            ? BitConverter.ToUInt16(signalHeader, 6)
            : (ushort)0;
        byte[] signalGuard = signalHeader.Length == 12 &&
            signalAddress - 12 + signalAllocationLength + 16 <=
                ArmModemBus.InternalRamSize
            ? modem.Bus.SnapshotInternal(
                signalAddress - 12 + signalAllocationLength + 12,
                4)
            : [];
        driverPayloadAssembly.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} r0=0x{modem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{modem.Cpu.GetGpr(1):x8} primary={Convert.ToHexString(primary)} " +
            $"assembly={Convert.ToHexString(assembly)} signal=0x{signalAddress:x8} " +
            $"header={Convert.ToHexString(signalHeader)} " +
            $"guard={Convert.ToHexString(signalGuard)} " +
            $"bytes={Convert.ToHexString(signal)}");
    }
    else if (pc == 0x010cf290 && d2d6SignalSends.Count < 1_000)
    {
        uint stack = modem.Cpu.GetGpr(13);
        byte[] arguments = stack < ArmModemBus.InternalRamSize - 16
            ? modem.Bus.SnapshotInternal(stack, 16)
            : [];
        d2d6SignalSends.Add(
            $"stage={stage} cycle={modem.Cycles:n0} r0=0x{modem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{modem.Cpu.GetGpr(1):x8} r2=0x{modem.Cpu.GetGpr(2):x8} " +
            $"r3=0x{modem.Cpu.GetGpr(3):x8} stack={Convert.ToHexString(arguments)}");
    }
    else if (pc == 0x010c0312 && secondaryTransferHandlers.Count < 1_000)
    {
        secondaryTransferHandlers.Add(
            $"stage={stage} cycle={modem.Cycles:n0} r0=0x{modem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{modem.Cpu.GetGpr(1):x8} r2=0x{modem.Cpu.GetGpr(2):x8} " +
            $"r3=0x{modem.Cpu.GetGpr(3):x8} lr=0x{modem.Cpu.GetGpr(14):x8}");
    }
    else if (pc == 0x010be0cc && driverC3efContexts.Count < 1_000)
    {
        uint stack = modem.Cpu.GetGpr(13);
        uint signalAddress = stack < ArmModemBus.InternalRamSize - 0x14
            ? BitConverter.ToUInt32(modem.Bus.SnapshotInternal(stack + 0x10, 4))
            : 0;
        byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 24
            ? modem.Bus.SnapshotInternal(signalAddress, 24)
            : [];
        if (signal.Length >= 2 && BitConverter.ToUInt16(signal, 0) == 0xc3ef)
        {
            if (setC3efState3)
            {
                // Diagnostic-only: exercise the firmware's own state-3
                // branch at the precise C3EF dispatch boundary. This does
                // not belong in the peripheral model until the preceding
                // controller state transition is recovered.
                modemBus.WritePeripheralSharedExternal(0x01400c96, [3]);
            }
            c3efReceived = true;
            c3efInstructionTraceRemaining = 96;
            pendingC3efCompletion = acknowledgeC3efCompletion;
            if (injectDspTransfersAfterC3ef && !dspTransfersInjectedAfterC3ef)
            {
                // Diagnostic timing hook only: present the raw secondary
                // response on entry to C3EF, before its native error/timeout
                // path can retire the request.
                foreach (var transfer in injectedDspTransfers)
                {
                    modemBus.QueueDspInboundTransfer(
                        transfer.Control,
                        transfer.Kind,
                        transfer.Payload);
                }
                dspTransfersInjectedAfterC3ef = true;
                postC3efWorkInjections.Add(
                    $"cycle={modem.Cycles:n0} raw-transfer=queued-at-c3ef");
            }
            driverC3efContexts.Add(
                $"stage={stage} cycle={modem.Cycles:n0} signal=0x{signalAddress:x8} " +
                $"bytes={Convert.ToHexString(signal)} gate=0x" +
                modem.Bus.SnapshotExternal(0x01400c54, 1)[0].ToString("x2") +
                " context=" +
                Convert.ToHexString(modem.Bus.SnapshotExternal(0x01400c40, 0x60)) +
                (setC3efState3 ? " c3ef-state=03" : string.Empty));
        }
    }
    else if (pc == 0x010be0d6 && c3efReceived && c3efStateBranchTrace.Count < 32)
    {
        c3efStateBranchTrace.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} before-read " +
            $"shared-state=0x{modem.Bus.SnapshotExternal(0x01400c96, 1)[0]:x2}");
    }
    else if (pc == 0x010be0d8 && c3efReceived && c3efStateBranchTrace.Count < 32)
    {
        c3efStateBranchTrace.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} after-read " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8}");
    }
    else if (pc == 0x010be0f0 && pendingC3efCompletion)
    {
        // 010be0ea has just acknowledged the prior status-08 edge. The
        // controller completion must be a subsequent edge, not a bit that
        // was already pending when the ARM performed that write.
        modemBus.AssertDspStatus(ArmModemBluetoothPeripheral.FixedTransferCompletionStatus);
        pendingC3efCompletion = false;
    }
    else if (pc == 0x010be102 && c3efReceived && forcePostC3efWorkPending)
    {
        // Diagnostic-only: sample the transfer branch after C3EF's LMP
        // decoder has returned and initialized the scheduler work area.
        // This is not a recovered peripheral protocol field.
        modemBus.WritePeripheralSharedExternal(0x01400c60, [2]);
        postC3efWorkInjections.Add(
            $"cycle={modem.Cycles:n0} work=" +
            Convert.ToHexString(modem.Bus.SnapshotExternal(0x01400c60, 4)));
    }
    else if (pc == 0x010be102 && c3efReceived && forcePostC3efSchedulerPending)
    {
        // Diagnostic-only: C3EF leaves the scheduler's signed pending byte
        // clear. Raising it exposes the separate scheduler branch without
        // claiming it is the real Bluetooth peripheral notification.
        modemBus.WritePeripheralSharedExternal(0x01400c4b, [1]);
        postC3efWorkInjections.Add(
            $"cycle={modem.Cycles:n0} scheduler=" +
            Convert.ToHexString(modem.Bus.SnapshotExternal(0x01400c48, 8)));
    }
    if (c3efReceived &&
        (pc is 0x010c0200 or 0x010c02ca or 0x010c030c or 0x010c0312) &&
        postC3efTransferPath.Count < 100)
    {
        postC3efTransferPath.Add(
            $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            "state=" +
            Convert.ToHexString(modem.Bus.SnapshotExternal(0x01400c40, 0x60)));
    }
    if (pc == 0x010bbd9a)
    {
        uint recordAddress = modem.Cpu.GetGpr(2);
        activeControllerRecord = recordAddress < ArmModemBus.InternalRamSize -
            ArmModemBluetoothPeripheral.ControllerRecordLength
            ? modem.Bus.SnapshotInternal(
                recordAddress,
                ArmModemBluetoothPeripheral.ControllerRecordLength)
            : recordAddress is >= ArmModemBus.ExternalRamBase and
                < ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan -
                    ArmModemBluetoothPeripheral.ControllerRecordLength
                ? modem.Bus.SnapshotExternal(
                    recordAddress,
                    ArmModemBluetoothPeripheral.ControllerRecordLength)
                : [];
    }
    else if (pc == 0x010c1ad4 &&
        modem.Cpu.GetGpr(14) is >= 0x010bbd00 and < 0x010bc900 &&
        decodedControllerRecords.Count < 4_000)
    {
        uint signalAddress = modem.Cpu.GetGpr(0);
        byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 16
            ? modem.Bus.SnapshotInternal(signalAddress, 16)
            : [];
        decodedControllerRecords.Add(
            $"stage={stage} cycle={modem.Cycles:n0} " +
            $"record={Convert.ToHexString(activeControllerRecord)} " +
            $"signal=0x{(signal.Length >= 2 ? BitConverter.ToUInt16(signal, 0) : 0xffff):x4} " +
            $"bytes={Convert.ToHexString(signal)}");
    }
    if (actionInstructionTraceRemaining > 0 &&
        actionInstructionTrace.Count < 40_000)
    {
        actionInstructionTrace.Add(
            $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r3=0x{modem.Cpu.GetGpr(3):x8} " +
            $"r4=0x{modem.Cpu.GetGpr(4):x8} r7=0x{modem.Cpu.GetGpr(7):x8} " +
            $"lr=0x{modem.Cpu.GetGpr(14):x8}");
        actionInstructionTraceRemaining--;
    }
    if (signalReceiveTraceRemaining > 0 &&
        signalReceiveTrace.Count < 4_000)
    {
        signalReceiveTrace.Add(
            $"cycle={modem.Cycles:n0} pc=0x{pc:x8} r0=0x{modem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{modem.Cpu.GetGpr(1):x8} r7=0x{modem.Cpu.GetGpr(7):x8} " +
            $"lr=0x{modem.Cpu.GetGpr(14):x8}");
        signalReceiveTraceRemaining--;
    }
    if (tracedSignal == 0xc3dd &&
        pc is 0x010a9190 or 0x010a9194 or 0x010a919c or
            0x010a91a0 or 0x010a91a6 or 0x010a91ac or
            0x010a91b0 or 0x010a91d0 or 0x010a932e &&
        signalReceiveTrace.Count < 4_000)
    {
        uint channel = modem.Cpu.GetGpr(7);
        signalReceiveTrace.Add(
            $"channel-step cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} r7=0x{channel:x8} " +
            $"bytes={Convert.ToHexString(SnapshotArmMemory(modem.Bus, channel, 16))}");
    }
    if (pc is 0x01001e7c or 0x01001e94 or 0x01001dda &&
        oseSignalReceives.Count < 4_000)
    {
        uint signalAddress = modem.Cpu.GetGpr(0);
        if (signalAddress < ArmModemBus.InternalRamSize - 16 ||
            (signalAddress >= ArmModemBus.ExternalRamBase &&
             signalAddress < ArmModemBus.ExternalRamBase +
                 ArmModemBus.ExternalRamMirrorSpan - 16))
        {
            byte[] signal = signalAddress < ArmModemBus.InternalRamSize
                ? modem.Bus.SnapshotInternal(signalAddress, 16)
                : modem.Bus.SnapshotExternal(signalAddress, 16);
            ushort signalId = BitConverter.ToUInt16(signal, 0);
            if ((tracedSignal is not null && signalId == tracedSignal) ||
                (tracedSignal is null && stage == "bluetooth-discovery" &&
                 signalId == 0xc3ea))
            {
                signalReceiveTrace.Clear();
                signalReceiveTraceRemaining = 2_000;
            }
            oseSignalReceives.Add(
                $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
                $"signal=0x{BitConverter.ToUInt16(signal, 0):x4} " +
                $"lr=0x{modem.Cpu.GetGpr(14):x8} " +
                $"r1=0x{modem.Cpu.GetGpr(1):x8} " +
                $"bytes={Convert.ToHexString(signal)}");
        }
    }
    else if (pc == 0x010adb72 && linkManagerActions.Count < 2_000)
    {
        uint signalAddress = modem.Cpu.GetGpr(1);
        ushort signal = signalAddress < ArmModemBus.InternalRamSize - 2
            ? BitConverter.ToUInt16(modem.Bus.SnapshotInternal(signalAddress, 2))
            : (ushort)0xffff;
        linkManagerActions.Add(
            $"stage={stage} cycle={modem.Cycles:n0} action=0x{modem.Cpu.GetGpr(0):x4} " +
            $"signal=0x{signal:x4} address=0x{signalAddress:x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} r4=0x{modem.Cpu.GetGpr(4):x8}");
        if (modem.Cpu.GetGpr(0) == 0x0016 &&
            pairingDspRecordIndex < pairingDspRecords.Length &&
            pairingDspRecordInstruction == long.MaxValue)
        {
            // Diagnostic only: action 0016 is the native C3E5 transition
            // into runtime state 09, where the recovered pairing records are
            // accepted. Queue them later through the real DSP FIFO.
            pairingDspRecordInstruction =
                machine.ExecutedInstructions + 500_000;
        }
        if (emulateRemoteName && modem.Cpu.GetGpr(0) == 0x0c)
        {
            remoteNameRecordInstruction = machine.ExecutedInstructions + 500_000;
        }
        if (tracedAction == modem.Cpu.GetGpr(0) &&
            (tracedActionStage is null || tracedActionStage == stage) &&
            actionInstructionTrace.Count == 0)
        {
            actionInstructionTraceRemaining = 30_000;
        }
    }
    else if (pc == 0x01003040 &&
             stage != "boot" &&
             oseSignalSends.Count < 4_000)
    {
        uint pointerAddress = modem.Cpu.GetGpr(0);
        if (pointerAddress >= ArmModemBus.InternalRamSize - 4)
        {
            byte[] pointer = pointerAddress is >= ArmModemBus.ExternalRamBase and
                < ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan - 4
                ? modem.Bus.SnapshotExternal(pointerAddress, 4)
                : [];
            uint signalAddress = pointer.Length == 4
                ? BitConverter.ToUInt32(pointer)
                : uint.MaxValue;
            byte[] signal = signalAddress < ArmModemBus.InternalRamSize - 8
                ? modem.Bus.SnapshotInternal(signalAddress, 16)
                : signalAddress is >= ArmModemBus.ExternalRamBase and
                    < ArmModemBus.ExternalRamBase +
                        ArmModemBus.ExternalRamMirrorSpan - 16
                    ? modem.Bus.SnapshotExternal(signalAddress, 16)
                    : [];
            ushort signalId = signal.Length >= 2
                ? BitConverter.ToUInt16(signal, 0)
                : (ushort)0xffff;
            if (injectD2fbAfterD2fa && signalId == 0xd2fa)
            {
                InjectD2fb("before-D2FA");
            }
            oseSignalSends.Add(
                $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
                $"pid=0x{modem.Cpu.GetGpr(1):x4} " +
                $"signal-pointer=0x{pointerAddress:x8} signal=0x{signalId:x4} " +
                $"bytes={Convert.ToHexString(signal)}");
            if (tracedSendSignal == signalId &&
                actionInstructionTrace.Count == 0)
            {
                actionInstructionTraceRemaining = 1_500;
            }
        }
        else
        {
            uint signalAddress = BitConverter.ToUInt32(
                modem.Bus.SnapshotInternal(pointerAddress, 4));
            if (signalAddress >= ArmModemBus.InternalRamSize - 8)
            {
                oseSignalSends.Add(
                    $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
                    $"pid=0x{modem.Cpu.GetGpr(1):x4} " +
                    $"signal-pointer=0x{pointerAddress:x8} " +
                    $"signal=0x{signalAddress:x8} (non-internal)");
            }
            else
            {
                byte[] signal = modem.Bus.SnapshotInternal(signalAddress, 16);
                ushort signalId = BitConverter.ToUInt16(signal, 0);
                if (injectD2fbAfterD2fa && signalId == 0xd2fa)
                {
                    InjectD2fb("before-D2FA");
                }
                oseSignalSends.Add(
                    $"stage={stage} cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
                    $"pid=0x{modem.Cpu.GetGpr(1):x4} " +
                    $"signal=0x{signalId:x4} " +
                    $"bytes={Convert.ToHexString(signal)}");
                if (tracedSendSignal == signalId &&
                    actionInstructionTrace.Count == 0)
                {
                    actionInstructionTraceRemaining = 1_500;
                }
            }
        }
    }
};
machine.SecondaryPorts.OutputStateChanged += state =>
{
    var key = (stage, state.Register40, state.Register48, state.Register80);
    outputWrites[key] = outputWrites.GetValueOrDefault(key) + 1;
};
machine.PowerPorts.OutputStateChanged += state =>
{
    var key = (stage, state);
    powerOutputWrites[key] = powerOutputWrites.GetValueOrDefault(key) + 1;
};
machine.SecondaryPorts.OpaqueRegisterWritten += (register, value) =>
{
    var key = (stage, register, value);
    opaqueWrites[key] = opaqueWrites.GetValueOrDefault(key) + 1;
    if (opaqueWriteSequence.Count < 4_000)
    {
        opaqueWriteSequence.Add(
            $"stage={stage} cycle={machine.Cycles:n0} " +
            $"register=0x{register:x2} value=0x{value:x2}");
    }
};

Run(requestedStandbyInstructions ??
    (useLiveGsm ? 120_000_000 : 105_000_000));
SaveFrame("standby");
PrintCheckpoint("native standby");
if (connectExternalPowerAfterStandby)
{
    machine.PowerPorts.SetExternalPowerConnected(true);
    Run(40_000_000);
    SaveFrame("external-power-connected");
    PrintCheckpoint("external power connected");
}

stage = "menu-navigation";
Press("open-main-menu", center);
Press("main-right-1", right);
Press("main-right-2", right);
Press("main-down-1", down);
Press("main-down-2", down);
Press("select-connect-icon", left);
Press("open-connect", center);
Press("connect-down-1", down);
Press("select-bluetooth-row", down);
Press("open-bluetooth", center);
Press("bluetooth-down-1", down);
Press("bluetooth-down-2", down);
Press("select-options-row", down);
Press("open-options", center);
Press("open-operation-mode", center);
if (operationIndex == 0)
{
    Press("operation-up-off", up);
}
else if (operationIndex == 2)
{
    Press("operation-down-on", down);
}
PrintCheckpoint($"Bluetooth {requestedOperation} selected");

stage = $"bluetooth-{requestedOperation}";
Contact operationCommit = operationReturn switch
{
    "center" => center,
    "yes" => yes,
    "clear" => clear,
    "no" => no,
    _ => throw new ArgumentOutOfRangeException(
        nameof(args),
        $"Unknown operation return key '{operationReturn}'."),
};
Press(
    $"apply-operation-{requestedOperation}-{operationReturn}",
    operationCommit,
    postInstructions: 30_000_000);
PrintCheckpoint($"Bluetooth {requestedOperation} applied");

if (operationIndex == 2 && openPairedDevices)
{
    stage = "bluetooth-paired-devices";
    Press("back-to-bluetooth", no, postInstructions: 6_000_000);
    Press("bluetooth-up-to-discover", up, postInstructions: 6_000_000);
    Press("bluetooth-up-to-paired-devices", up, postInstructions: 6_000_000);
    Press("open-paired-devices", center, postInstructions: 12_000_000);
    SaveFrame("bluetooth-paired-devices");
    PrintCheckpoint("Bluetooth paired devices opened");
    if (addPairedDevice)
    {
        stage = "bluetooth-pairing-discovery";
        Press("add-paired-device", center, postInstructions: 40_000_000);
        SaveFrame("bluetooth-pairing-discovery");
        PrintCheckpoint("Bluetooth add paired device selected");
        if (startPairing)
        {
            Press("phone-initiates-pairing", center, postInstructions: 40_000_000);
            SaveFrame("bluetooth-pairing-device-types");
            PrintCheckpoint("Bluetooth phone-initiated pairing selected");
            Press("pair-all-device-types", center, postInstructions: 40_000_000);
            SaveFrame("bluetooth-pairing-search");
            PrintCheckpoint("Bluetooth all pairing device types selected");
        }
        if (waitForDiscovery)
        {
            stage = "bluetooth-pairing-response";
            Run(responseInstructions);
            SaveFrame("bluetooth-pairing-response");
            PrintCheckpoint("Bluetooth pairing discovery waited");
        }
    }
}

if (operationIndex == 2 &&
    !args.Contains("--skip-discovery", StringComparer.Ordinal))
{
    stage = "bluetooth-discovery";
    Press("back-to-bluetooth", no, postInstructions: 6_000_000);
    Press("select-discover", up, postInstructions: 6_000_000);
    Press("start-discover", center, postInstructions: 40_000_000);
    PrintCheckpoint("Bluetooth discovery requested");
    if (waitForDiscovery || injectedDspRecords.Length != 0 ||
        injectedDspTransfers.Length != 0 || injectDspRecordSweep ||
        injectDspRecordFlaggedSweep || injectDspRecordFullSweep ||
        appendDspRecordFullSweep ||
        emulateRemoteName ||
        injectedDspStatuses.Length != 0 ||
        scheduledDspStatuses.Count != 0 ||
        injectedControllerFrames.Length != 0 || injectedAsicFrames.Length != 0)
    {
        stage = "bluetooth-discovery-response";
        if (suppressIdleDspTransfers)
        {
            machine.BluetoothPeripheral!.EmitIdleTransferFrames = false;
        }
        if (responseInjectionDelayInstructions != 0)
        {
            Run(responseInjectionDelayInstructions);
        }
        foreach (var sharedByte in injectedDspSharedBytes)
        {
            modemBus.WritePeripheralSharedExternal(sharedByte.Address, [sharedByte.Value]);
        }
        foreach (byte status in injectedDspStatuses)
        {
            modemBus.AssertDspStatus(status);
        }
        if (injectedControllerFrames.Length != 0)
        {
            // Diagnostic-only: insert a complete native controller message at
            // the real UART boundary. This never patches firmware OSE state.
            foreach (var frame in injectedControllerFrames)
            {
                foreach (byte value in EncodeControllerFrame(frame))
                {
                    modemBus.QueueUart1ReceivedByte(value);
                }
            }
        }
        else if (injectedAsicFrames.Length != 0)
        {
            // Diagnostic-only: present an ARM-originated native-link record
            // to the unmodified AVR receiver, without touching AVR state.
            foreach (var frame in injectedAsicFrames)
            {
                foreach (byte value in EncodeAsicFrame(frame))
                {
                    machine.ByteChannels.QueueReceivedByte(1, value);
                }
            }
        }
        else if (injectedDspRecords.Length != 0 || injectedDspTransfers.Length != 0)
        {
            foreach (byte[] record in injectedDspRecords)
            {
                machine.QueueBluetoothControllerRecord(record);
            }
            // Raw DSP ingress diagnostic. This preserves the native
            // control/kind/length framing; it does not synthesize OSE state.
            foreach (var transfer in injectDspTransfersAfterC3ef
                         ? []
                         : injectedDspTransfers)
            {
                modemBus.QueueDspInboundTransfer(
                    transfer.Control,
                    transfer.Kind,
                    transfer.Payload);
            }
        }
        else if (injectDspRecordSweep || injectDspRecordFlaggedSweep ||
                 injectDspRecordFullSweep)
        {
            // Field-domain sweep: every valid selector is paired with a
            // nonzero payload so a state-dependent address/device path cannot
            // be hidden by allocator fill or an all-zero sentinel.
            int firstFlag = injectDspRecordFlaggedSweep ? 1 : 0;
            int lastId = injectDspRecordFullSweep ? 0x7f : 0x39;
            for (var id = 0; id <= lastId; id++)
            {
                byte[] record = new byte[ArmModemBluetoothPeripheral.ControllerRecordLength];
                record[0] = (byte)((id << 1) | firstFlag);
                for (var offset = 1; offset < record.Length; offset++)
                {
                    record[offset] = (byte)(id + offset);
                }
                machine.QueueBluetoothControllerRecord(record);
            }
        }
        Run(responseInstructions);
        if (nudgeDiscoveredPeer)
        {
            Press(
                "nudge-discovered-peer",
                down,
                postInstructions: 12_000_000);
            SaveFrame("nudged-discovered-peer");
        }
        if (sweepDiscoveryKeys)
        {
            foreach (var key in new[]
                     {
                         ("left", left),
                         ("right", right),
                         ("up", up),
                         ("down", down),
                         ("center", center),
                         ("yes", yes),
                         ("clear", clear),
                         ("no", no),
                     })
            {
                Press(
                    $"discovery-key-{key.Item1}",
                    key.Item2,
                    postInstructions: 2_000_000);
            }
        }
        if (selectDiscoveredPeer)
        {
            Press(
                "select-discovered-peer",
                center,
                postInstructions: 12_000_000);
            SaveFrame("selected-discovered-peer");
            if (addDiscoveredPeer)
            {
                Press(
                    "add-discovered-peer",
                    center,
                    postInstructions: 30_000_000);
                SaveFrame("added-discovered-peer");
                if (pairingPasskey is not null)
                {
                    foreach (char digit in pairingPasskey)
                    {
                        Press(
                            $"pairing-passkey-{digit}",
                            digitContacts[digit],
                            postInstructions: 1_500_000);
                    }
                    Press(
                        "submit-pairing-passkey",
                        center,
                        postInstructions: pairingPostInstructions ??
                            Math.Max(
                                40_000_000L,
                                1_000_000L +
                                pairingDspRecords.Length * 6_000_000L));
                    SaveFrame("pairing-passkey-submitted");
                    for (var index = 0; index < postPairingKeys.Length; index++)
                    {
                        string keyName = postPairingKeys[index];
                        if (keyName.StartsWith("wait:", StringComparison.Ordinal))
                        {
                            long waitInstructions =
                                Convert.ToInt64(keyName["wait:".Length..]);
                            if (waitInstructions <= 0)
                            {
                                throw new ArgumentOutOfRangeException(
                                    nameof(args),
                                    "An ordered post-pairing wait must be positive.");
                            }
                            Run(waitInstructions);
                            SaveFrame($"post-pairing-{index + 1}-wait");
                            PrintCheckpoint(
                                $"Bluetooth post-pairing wait {index + 1}");
                            continue;
                        }
                        Contact key =
                            keyName.Length == 1 &&
                            digitContacts.TryGetValue(keyName[0], out Contact digit)
                                ? digit
                                : keyName switch
                                {
                                    "center" => center,
                                    "yes" => yes,
                                    "no" => no,
                                    "clear" => clear,
                                    "up" => up,
                                    "down" => down,
                                    "left" => left,
                                    "right" => right,
                                    _ => throw new ArgumentOutOfRangeException(
                                        nameof(args),
                                        $"Unknown post-pairing key '{keyName}'."),
                                };
                        stage = $"bluetooth-post-pairing-{index + 1}-{keyName}";
                        Press(
                            $"post-pairing-{index + 1}-{keyName}",
                            key,
                            postInstructions: postPairingKeyInstructions);
                        PrintCheckpoint(
                            $"Bluetooth post-pairing key {index + 1}: {keyName}");
                    }
                    if (postPairingWaitInstructions != 0)
                    {
                        Run(postPairingWaitInstructions);
                        SaveFrame("post-pairing-wait");
                        PrintCheckpoint("Bluetooth post-pairing wait");
                    }
                }
            }
        }
        if (stopDiscovery)
        {
            Press(
                "stop-discovery",
                stopDiscoveryWithClear
                    ? clear
                    : stopDiscoveryWithYes
                        ? yes
                        : no,
                postInstructions: 12_000_000);
            if (acknowledgeStoppedDiscovery)
            {
                Press(
                    "acknowledge-stopped-discovery",
                    yes,
                    postInstructions: 6_000_000);
            }
        }
        SaveFrame("bluetooth-discovery-response");
        PrintCheckpoint(waitForDiscovery
            ? "Bluetooth discovery waited without injected response"
            : injectedDspRecords.Length == 0
            ? injectDspRecordFullSweep
                ? "Bluetooth discovery full response sweep injected"
                : injectDspRecordFlaggedSweep
                    ? "Bluetooth discovery flagged response sweep injected"
                    : injectedDspTransfers.Length != 0
                        ? "Bluetooth DSP transfer response injected"
                    : injectedAsicFrames.Length != 0
                        ? "Bluetooth GUI link response injected"
                        : injectedControllerFrames.Length != 0
                            ? "Bluetooth controller-link response injected"
                            : "Bluetooth discovery response sweep injected"
            : "Bluetooth discovery response injected");
    }
}

Print("DSP packets", packets, key =>
    $"stage={key.Stage} type=0x{key.Type:x2} payload={key.Payload}");
Print("DSP secondary transfers", transfers, key =>
    $"stage={key.Stage} control=0x{key.Control:x2} kind={key.Kind} " +
    $"trailer=0x{key.Trailer:x2} payload={key.Payload}");
Print("Native BT_Ctrl DSP command ring", nativeDspCommands, key =>
    $"stage={key.Stage} type=0x{key.Type:x2} payload={key.Payload}");
Console.WriteLine("Native BT_Ctrl DSP command sequence:");
foreach (string entry in nativeDspCommandSequence)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Peer-initiated RFCOMM diagnostic:");
foreach (string entry in incomingRfcommDiagnosticEvents)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("AVR Bluetooth-name memory locations:");
ReadOnlySpan<byte> emulatedPeerName = "Mia Peer"u8;
ReadOnlySpan<byte> avrData = machine.Cpu.Data;
var nameSearchOffset = 0;
while (nameSearchOffset <= avrData.Length - emulatedPeerName.Length)
{
    int relativeOffset = avrData[nameSearchOffset..].IndexOf(emulatedPeerName);
    if (relativeOffset < 0)
    {
        break;
    }
    nameSearchOffset += relativeOffset;
    Console.WriteLine($"  0x{nameSearchOffset:x6}");
    nameSearchOffset++;
}
foreach (var (label, address, length) in new[]
         {
             ("discovery-layout-parent", 0x03880b, 0x100),
             ("discovery-layout-node", 0x024b31, 0x100),
             ("discovery-layout-result", 0x024b56, 0x100),
         })
{
    Console.WriteLine(
        $"{label}=0x{address:x6}:" +
        Convert.ToHexString(avrData.Slice(address, length)));
}
Print("0x92 output states", outputWrites, key =>
    $"stage={key.Stage} 40=0x{key.Register40:x2} " +
    $"48=0x{key.Register48:x2} 80=0x{key.Register80:x2}");
Print("Primary power-port output states", powerOutputWrites, key =>
    $"stage={key.Stage} a3=0x{key.State.PortA3:x2} " +
    $"a4=0x{key.State.PortA4:x2} a5=0x{key.State.PortA5:x2} " +
    $"a6=0x{key.State.PortA6:x2} a7=0x{key.State.PortA7:x2} " +
    $"a9=0x{key.State.PortA9:x2} power=0x{key.State.PowerControl:x2} " +
    $"b0=0x{key.State.PortB0:x2}");
Print("0x92 opaque writes", opaqueWrites, key =>
    $"stage={key.Stage} register=0x{key.Register:x2} value=0x{key.Value:x2}");
Console.WriteLine("0x92 opaque write sequence:");
foreach (string entry in opaqueWriteSequence)
{
    Console.WriteLine("  " + entry);
}
PrintBytes("ARM UART1 transmitted", uartTransmitBytes);
PrintBytes("ARM UART1 received", uartReceiveBytes);
Console.WriteLine("AVR-to-ARM Bluetooth-controller frames:");
foreach (var frame in bluetoothControllerFrames)
{
    Console.WriteLine("  " + frame);
}
Console.WriteLine("ARM-to-ASIC link frames:");
foreach (var frame in modemToAsicFrames)
{
    Console.WriteLine("  " + frame);
}
Console.WriteLine("Link-manager actions:");
foreach (string entry in linkManagerActions)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Decoded Bluetooth controller records:");
foreach (string entry in decodedControllerRecords)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT request dispatcher entries:");
foreach (string entry in btreqRequestDispatches)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bond-database request entries:");
foreach (string entry in bondDatabaseRequests)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bond-database state writes:");
foreach (string entry in bondDatabaseWrites)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bond-database instruction trace:");
foreach (string entry in bondDatabaseInstructionTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("D2D6 local signal sender entries:");
foreach (string entry in d2d6SignalSends)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("DSP secondary-transfer handler entries:");
foreach (string entry in secondaryTransferHandlers)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl C3EF receive contexts:");
foreach (string entry in driverC3efContexts)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl C3EF shared-state branch trace:");
foreach (string entry in c3efStateBranchTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl C3EF instruction trace:");
foreach (string entry in c3efInstructionTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl post-C3EF transfer-path snapshots:");
foreach (string entry in postC3efTransferPath)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl post-C3EF work injections:");
foreach (string entry in postC3efWorkInjections)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Forced C3DD dispatches:");
foreach (string entry in forcedC3ddDispatches)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl receive-gate changes (state+0x14):");
foreach (string entry in driverReceiveGateChanges)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl context writes:");
foreach (string entry in driverContextWrites)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Link-manager external-context writes:");
foreach (string entry in linkManagerContextWrites)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Link-manager C3F6-producer writes during response:");
foreach (string entry in linkManagerProducerWrites)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl payload assembly:");
foreach (string entry in driverPayloadAssembly)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl allocator guard writes:");
foreach (string entry in allocatorGuardWrites)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("C3DD OSE-header lifecycle:");
foreach (string entry in c3ddHeaderLifecycle)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("C3F6 producer contexts:");
foreach (string entry in c3f6ProducerContexts)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl payload-assembly writes:");
foreach (string entry in driverAssemblyWrites)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl indexed-DSP accesses:");
foreach (string entry in dspIndexedAccesses)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl inbound-DSP FIFO reads:");
foreach (string entry in dspInboundFifoAccesses)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("ARM OSE signal sends:");
foreach (string entry in oseSignalSends)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("ARM OSE signal receives:");
foreach (string entry in oseSignalReceives)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bonded reconnect OSE mailbox trace:");
foreach (string entry in reconnectMailboxTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("BT_Ctrl task-loop trace after bonded reconnect:");
foreach (string entry in driverTaskLoopTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine($"Signal receive instruction trace ({tracedSignal ?? 0xc3ea:x4}):");
foreach (string entry in signalReceiveTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("C40C native consumer matches:");
foreach (string entry in c40cConsumerMatches)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bluetooth state-table searches:");
foreach (string entry in bluetoothStateTableSearches)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("DSP wire event trace:");
foreach (string entry in dspWireEvents)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Pairing DSP wire events:");
foreach (string entry in pairingDspWireEvents)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bonded reconnect DSP transport trace:");
foreach (string entry in reconnectDspTransportTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Scheduled DSP status events:");
foreach (string entry in scheduledDspStatusEvents)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Link-manager action instruction trace:");
foreach (string entry in actionInstructionTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bluetooth E1 calls:");
foreach (string entry in bluetoothE1Calls)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Bluetooth key-derivation calls:");
foreach (string entry in bluetoothKeyDerivationCalls)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine(
    tracedAvrBluetoothSignal is { } printedAvrSignal
        ? $"AVR {printedAvrSignal:X4} instruction/SWBP trace:"
        : "AVR SWBP endpoint trace:");
foreach (string entry in avrD2ffTrace)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Discovery LCD scan transactions:");
foreach (string entry in discoveryDisplayTransactions)
{
    Console.WriteLine("  " + entry);
}
Console.WriteLine("Discovery native framebuffer writers:");
foreach (var pair in discoveryFramebufferWriters
             .OrderBy(pair => pair.Value.FirstInstruction)
             .ThenBy(pair => pair.Key))
{
    Console.WriteLine(
        $"  pc=0x{pair.Key:x6} count={pair.Value.Count:n0} " +
        $"offsets=0x{pair.Value.MinOffset:x4}..0x{pair.Value.MaxOffset:x4} " +
        $"first-i={pair.Value.FirstInstruction:n0} " +
        $"last-i={pair.Value.LastInstruction:n0}");
}
Console.WriteLine("Discovery graphics packets:");
foreach (string entry in discoveryGraphicsPackets)
{
    Console.WriteLine("  " + entry);
}
if (machine.CommandPort.RejectedPacketCount != observedGraphicsRejections)
{
    discoveryGraphicsRejections.Add(
        $"end-i={machine.ExecutedInstructions:n0} " +
        $"count={machine.CommandPort.RejectedPacketCount:n0} delta=" +
        $"{machine.CommandPort.RejectedPacketCount - observedGraphicsRejections:n0} " +
        $"previous={previousGraphicsPacket}");
}
Console.WriteLine("Discovery graphics rejections:");
foreach (string entry in discoveryGraphicsRejections)
{
    Console.WriteLine("  " + entry);
}
if (avrDump is { } requestedAvrDump)
{
    byte[] bytes = Enumerable.Range(0, requestedAvrDump.Length)
        .Select(offset => machine.Cpu.ReadData(requestedAvrDump.Address + offset))
        .ToArray();
    Console.WriteLine(
        $"AVR data 0x{requestedAvrDump.Address:x6}: " +
        Convert.ToHexString(bytes));
}

return 0;

static byte[] SnapshotArmMemory(
    ArmModemBus bus,
    uint address,
    int length)
{
    if (address < ArmModemBus.InternalRamSize - length)
    {
        return bus.SnapshotInternal(address, length);
    }
    if (address >= ArmModemBus.ExternalRamBase &&
        address < ArmModemBus.ExternalRamBase +
            ArmModemBus.ExternalRamMirrorSpan - length)
    {
        return bus.SnapshotExternal(address, length);
    }
    return [];
}

static uint ReadArmUInt32(ArmModemBus bus, uint address)
{
    byte[] bytes = SnapshotArmMemory(bus, address, sizeof(uint));
    return bytes.Length == sizeof(uint)
        ? BitConverter.ToUInt32(bytes)
        : uint.MaxValue;
}

static string DescribeBluetoothOutput(ArmModemBus bus)
{
    const uint contextAddress = 0x01400c40;
    const uint slotBaseAddress = 0x014215ec;
    const int slotLength = 0x13;
    byte[] context = bus.SnapshotExternal(contextAddress, 0x60);
    ushort producer = BitConverter.ToUInt16(context, 0x30);
    ushort consumer = BitConverter.ToUInt16(context, 0x32);
    short count = BitConverter.ToInt16(context, 0x34);
    byte[] slot = bus.SnapshotExternal(
        slotBaseAddress + (uint)(consumer * slotLength),
        slotLength);
    return
        $"producer={producer} consumer={consumer} count={count} " +
        $"state=0x{context[0x56]:x2} slot[{consumer}]=" +
        Convert.ToHexString(slot);
}

static string DescribeOseTask(ArmModemBus bus, uint taskAddress)
{
    byte[] task = SnapshotArmMemory(bus, taskAddress, 0x54);
    if (task.Length != 0x54)
    {
        return $"tcb=0x{taskAddress:x8}:unmapped";
    }

    var signals = new List<string>();
    uint header = BitConverter.ToUInt32(task, 0);
    for (var index = 0;
         index < 8 && header != 0 && header != uint.MaxValue;
         index++)
    {
        byte[] signalHeader = SnapshotArmMemory(bus, header, 14);
        if (signalHeader.Length != 14)
        {
            signals.Add($"0x{header:x8}:unmapped");
            break;
        }
        signals.Add(
            $"0x{header:x8}->0x{BitConverter.ToUInt32(signalHeader, 0):x8}" +
            $":{BitConverter.ToUInt16(signalHeader, 12):x4}");
        header = BitConverter.ToUInt32(signalHeader, 0);
    }

    return
        $"tcb=0x{taskAddress:x8} head=0x{BitConverter.ToUInt32(task, 0):x8} " +
        $"tail=0x{BitConverter.ToUInt32(task, 4):x8} " +
        $"flags=0x{BitConverter.ToUInt16(task, 8):x4} " +
        $"priority=0x{task[0x0b]:x2} wait=0x{BitConverter.ToUInt16(task, 0x18):x4} " +
        $"filter=0x{BitConverter.ToUInt32(task, 0x10):x8} " +
        $"queue=[{string.Join(",", signals)}]";
}

static string DescribeOseKernel(ArmModemBus bus, uint kernelAddress)
{
    byte[] kernel = SnapshotArmMemory(bus, kernelAddress, 0x48);
    return kernel.Length == 0x48
        ? $"base=0x{kernelAddress:x8} ready=0x{BitConverter.ToUInt32(kernel, 0x10):x8} " +
          $"current=0x{BitConverter.ToUInt32(kernel, 0x40):x8} " +
          $"selected=0x{BitConverter.ToUInt32(kernel, 0x44):x8}"
        : $"base=0x{kernelAddress:x8}:unmapped";
}

static int GraphicsPacketLength(byte opcode) => opcode switch
{
    0x05 => 3,
    0x06 => 5,
    0x0a => 4,
    0x0b => 2,
    0x0c => 2,
    0x0d => 1,
    0x0e => 1,
    0x0f => 5,
    0x10 => 12,
    0x11 => 9,
    0x12 => 3,
    0x15 => 1,
    0x4c => 3,
    0x80 => 3,
    _ => 0,
};

static (byte Source, byte[] Payload) ParseControllerFrame(string text)
{
    string[] parts = text.Split(':', 2);
    if (parts.Length != 2)
    {
        throw new ArgumentException(
            "Frame must be SOURCE:PAYLOAD_HEX (for example 63:D4D2010203040506).");
    }

    return (Convert.ToByte(parts[0], 16), Convert.FromHexString(parts[1]));
}

static (byte Control, byte Kind, byte[] Payload) ParseDspTransfer(string text)
{
    string[] parts = text.Split(':', 3);
    if (parts.Length != 3 ||
        !byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out byte control) ||
        !byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte kind) ||
        kind > 7)
    {
        throw new ArgumentException(
            "DSP transfer must be CONTROL:KIND:PAYLOAD, with hexadecimal bytes.");
    }

    byte[] payload;
    try
    {
        payload = Convert.FromHexString(parts[2]);
    }
    catch (FormatException exception)
    {
        throw new ArgumentException("DSP transfer payload must be hexadecimal.", exception);
    }
    if (payload.Length > 0x1f)
    {
        throw new ArgumentException("DSP transfer payload must be at most 31 bytes.");
    }
    return (control, kind, payload);
}

static (byte Address, byte Value) ParseDspIndex(string text)
{
    string[] parts = text.Split(':', 2);
    if (parts.Length != 2 ||
        !byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out byte address) ||
        !byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte value))
    {
        throw new ArgumentException("DSP index must be ADDRESS:VALUE in hexadecimal.");
    }
    return (address, value);
}

static (byte State, byte Status) ParseDspStatusOnState(string text)
{
    string[] parts = text.Split(':', 2);
    if (parts.Length != 2 ||
        !byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out byte state) ||
        !byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte status))
    {
        throw new ArgumentException(
            "Scheduled DSP status must be STATE:STATUS in hexadecimal.");
    }
    return (state, status);
}

static (uint Address, byte Value) ParseDspSharedByte(string text)
{
    string[] parts = text.Split(':', 2);
    if (parts.Length != 2 ||
        !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint address) ||
        !byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte value) ||
        address < ArmModemBus.ExternalRamBase ||
        address >= ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan)
    {
        throw new ArgumentException(
            "DSP shared byte must be an external-RAM ADDRESS:VALUE in hexadecimal.");
    }
    return (address, value);
}

static byte[] EncodeControllerFrame((byte Source, byte[] Payload) frame)
{
    if (frame.Payload.Length > ushort.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(frame));
    }

    byte[] wire = new byte[6 + frame.Payload.Length];
    wire[0] = 0xab;
    wire[1] = 0xba;
    wire[2] = 0x86;
    wire[3] = (byte)frame.Payload.Length;
    wire[4] = (byte)(frame.Payload.Length >> 8);
    wire[5] = frame.Source;
    frame.Payload.CopyTo(wire, 6);
    return wire;
}

void InjectD2fb(string timing)
{
    if (d2fbInjected)
    {
        return;
    }
    foreach (byte value in EncodeAsicFrame((0x63, [0xfb, 0xd2])))
    {
        machine.ByteChannels.QueueReceivedByte(1, value);
    }
    d2fbInjected = true;
    scheduledDspStatusEvents.Add(
        $"cycle={machine.Modem!.Cycles:n0} diagnostic=D2FB-{timing}");
}

static byte[] EncodeAsicFrame((byte Source, byte[] Payload) frame)
{
    if (frame.Payload.Length > ushort.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(frame));
    }

    byte[] wire = new byte[4 + frame.Payload.Length];
    wire[0] = (byte)(frame.Source | 0x80);
    wire[1] = (byte)frame.Payload.Length;
    wire[2] = (byte)(frame.Payload.Length >> 8);
    wire[3] = 0x06; // ARM link-process source observed in D306 replies.
    frame.Payload.CopyTo(wire, 4);
    return wire;
}

void Press(
    string actionName,
    Contact contact,
    long postInstructions = 1_500_000)
{
    if (tracedAvrBluetoothSignal is not null)
    {
        avrD2ffTrace.Add(
            $"key-action-start action={actionName} " +
            $"i={machine.ExecutedInstructions:n0}");
    }
    action = actionName;
    if (contact.Power)
    {
        machine.SetPowerKey(pressed: true);
    }
    else
    {
        machine.SetKey(
            contact.ScanMask,
            contact.RowMask,
            contact.SecondaryScanMask,
            pressed: true);
    }
    Run(900_000);
    var expectedContact = new InteractiveKeypadContact(
        contact.ScanMask,
        contact.RowMask,
        contact.SecondaryScanMask,
        contact.Power);
    deferredContact = expectedContact;
    InteractiveKeypadTransition release;
    if (contact.Power)
    {
        release = machine.SetPowerKey(pressed: false);
    }
    else
    {
        release = machine.SetKey(
            contact.ScanMask,
            contact.RowMask,
            contact.SecondaryScanMask,
            pressed: false);
    }
    if (release != InteractiveKeypadTransition.Deferred)
    {
        deferredContact = null;
    }
    else
    {
        var releaseDeadline = machine.ExecutedInstructions + 20_000_000;
        while (deferredContact is not null &&
               machine.ExecutedInstructions < releaseDeadline)
        {
            Run(262_144);
        }
        if (deferredContact is not null)
        {
            throw new InvalidOperationException(
                $"Firmware did not scan the {actionName} key before the release deadline.");
        }
    }
    Run(postInstructions);
    if (tracedAvrBluetoothSignal is not null)
    {
        avrD2ffTrace.Add(
            $"key-action-end action={actionName} " +
            $"i={machine.ExecutedInstructions:n0}");
    }
    SaveFrame(actionName);
}

void SaveFrame(string label)
{
    if (frameDirectory is null)
    {
        return;
    }

    var safeLabel = string.Concat(label.Select(character =>
        char.IsLetterOrDigit(character) ? character : '-'));
    var path = Path.Combine(
        frameDirectory,
        $"{++frameIndex:D2}-{safeLabel}.ppm");
    using var output = File.Create(path);
    using var header = new StreamWriter(output, leaveOpen: true);
    header.Write("P6\n101 80\n255\n");
    header.Flush();
    Span<byte> rgb = stackalloc byte[3];
    foreach (var pixel in machine.Frame.Span)
    {
        rgb[0] = (byte)((pixel >> 5) * 255 / 7);
        rgb[1] = (byte)(((pixel >> 2) & 7) * 255 / 7);
        rgb[2] = (byte)((pixel & 3) * 255 / 3);
        output.Write(rgb);
    }
}

void Run(long instructions)
{
    var end = machine.ExecutedInstructions + instructions;
    while (!machine.IsStopped && machine.ExecutedInstructions < end)
    {
        machine.RunWorkItems((int)Math.Min(262_144, end - machine.ExecutedInstructions));
        if (injectPeerObjectPushSabm &&
            !peerObjectPushPnInjected &&
            outboundL2capProbe.TryCreatePeerObjectPushPn(
                out byte[] peerPnPacket) &&
            modemBus.DspInboundTransferByteCount == 0)
        {
            // PN is mandatory before opening a credit-based server DLC.
            // The values mirror the handset's native PN request, while the
            // DLCI and L2CAP CID come from the recovered peer direction.
            modemBus.QueueDspInboundTransfer(
                0,
                L2capOutboundProbe.InitialTransferKind,
                peerPnPacket);
            peerObjectPushPnInjected = true;
            incomingRfcommDiagnosticEvents.Add(
                $"stage={stage} i={machine.ExecutedInstructions:n0} " +
                $"cycle={modemBus.Cycles:n0} injected-peer-pn-kind=6 " +
                $"payload={Convert.ToHexString(peerPnPacket)}");
        }
        if (injectPeerObjectPushSabm &&
            peerObjectPushPnInjected &&
            !peerObjectPushSabmInjected &&
            outboundL2capProbe.TryCreatePeerObjectPushSabm(
                out byte[] peerSabmPacket) &&
            modemBus.DspInboundTransferByteCount == 0)
        {
            // Diagnostic-only peer ingress at the recovered DSP/L2CAP
            // boundary. The packet is derived from the live handset CID and
            // ARM RFCOMM's 010c5960/010c3db4 direction rules; it does not
            // patch any firmware task, mailbox, or UI state.
            modemBus.QueueDspInboundTransfer(
                0,
                L2capOutboundProbe.InitialTransferKind,
                peerSabmPacket);
            peerObjectPushSabmInjected = true;
            incomingRfcommDiagnosticEvents.Add(
                $"stage={stage} i={machine.ExecutedInstructions:n0} " +
                $"cycle={modemBus.Cycles:n0} injected-kind=6 " +
                $"payload={Convert.ToHexString(peerSabmPacket)}");
        }
        if (injectPeerObjectPushConnect &&
            peerObjectPushSabmInjected &&
            !peerObjectPushConnectInjected &&
            outboundL2capProbe.TryCreatePeerObexConnect(
                out byte[] peerObexConnectPacket) &&
            modemBus.DspInboundTransferByteCount == 0)
        {
            modemBus.QueueDspInboundTransfer(
                0,
                L2capOutboundProbe.InitialTransferKind,
                peerObexConnectPacket);
            peerObjectPushConnectInjected = true;
            incomingRfcommDiagnosticEvents.Add(
                $"stage={stage} i={machine.ExecutedInstructions:n0} " +
                $"cycle={modemBus.Cycles:n0} injected-obex-connect-kind=6 " +
                $"payload={Convert.ToHexString(peerObexConnectPacket)}");
        }
        if (injectPeerObjectPushPut &&
            peerObjectPushConnectInjected &&
            !peerObjectPushPutQueued &&
            outboundL2capProbe.TryCreatePeerObexFinalPut(
                out byte[] peerObexPutPacket))
        {
            foreach (var fragment in
                     L2capOutboundProbe.FragmentInbound(peerObexPutPacket))
            {
                peerObjectPushPutFragments.Enqueue(fragment);
            }
            peerObjectPushPutQueued = true;
            incomingRfcommDiagnosticEvents.Add(
                $"stage={stage} i={machine.ExecutedInstructions:n0} " +
                $"cycle={modemBus.Cycles:n0} queued-obex-final-put " +
                $"payload={Convert.ToHexString(peerObexPutPacket)}");
        }
        if (peerObjectPushPutFragments.TryPeek(out var peerObexPutFragment) &&
            modemBus.DspInboundTransferByteCount == 0)
        {
            peerObjectPushPutFragments.Dequeue();
            modemBus.QueueDspInboundTransfer(
                0,
                peerObexPutFragment.Kind,
                peerObexPutFragment.Payload);
            incomingRfcommDiagnosticEvents.Add(
                $"stage={stage} i={machine.ExecutedInstructions:n0} " +
                $"cycle={modemBus.Cycles:n0} injected-obex-put-kind=" +
                $"{peerObexPutFragment.Kind} payload=" +
                Convert.ToHexString(peerObexPutFragment.Payload));
        }
    }
    if (machine.IsStopped)
    {
        throw new InvalidOperationException(machine.StopReason);
    }
}

void PrintCheckpoint(string label)
{
    var gsm = machine.LiveGsm?.GetStatus(machine);
    var indicators = machine.StatusIndicators.Diagnostics;
    Console.WriteLine(
        $"checkpoint={label}; stage={stage}; instructions={machine.ExecutedInstructions:n0}; " +
        $"cycles={machine.Cycles:n0}; frame={machine.FrameVersion}; " +
        $"frame-hash={Convert.ToHexStringLower(SHA256.HashData(machine.Frame.Span))}; " +
        $"graphics-bytes={machine.CommandPort.WrittenByteCount:n0}; " +
        $"graphics-packets={machine.CommandPort.CompletedPacketCount:n0}; " +
        $"graphics-rejected={machine.CommandPort.RejectedPacketCount:n0}; " +
        $"graphics-rasters={machine.CommandPort.RasterOperationCount:n0}; " +
        $"graphics-pixels={machine.CommandPort.PixelWriteCount:n0}; " +
        $"registered={gsm?.Registered}; rr=0x{gsm?.RrState:x2}; " +
        $"mph=0x{gsm?.MphState:x2}; secondary=" +
        $"40:{machine.SecondaryPorts.OutputState.Register40:x2}," +
        $"48:{machine.SecondaryPorts.OutputState.Register48:x2}," +
        $"80:{machine.SecondaryPorts.OutputState.Register80:x2}; " +
        $"network-green={indicators.State.NetworkGreen}; " +
        $"bluetooth-blue={indicators.State.BluetoothBlue}; " +
        $"charging-active={indicators.ChargingActive}; " +
        $"bluetooth-steady={indicators.BluetoothSteadyDueToCharging}; " +
        $"external-power={machine.PowerPorts.ExternalPowerConnected}; " +
        $"power-revision=0x{machine.PowerPorts.SiliconRevision:x2}; " +
        $"power-interrupt-mask=0x{machine.PowerPorts.InterruptMask:x2}; " +
        $"power-interrupts={machine.PowerPorts.InterruptCount:n0}; " +
        $"power-edge-reads={machine.PowerPorts.OnOffStatusReadCount:n0}; " +
        $"power-status-reads={machine.PowerPorts.PortStatusReadCount:n0}; " +
        $"voltage-adc-reads={machine.PowerPorts.VoltageAdcReadCount:n0}; " +
        $"charging-current-reads=" +
        $"{machine.PowerPorts.ChargingCurrentAdcReadCount:n0}; " +
        $"battery-temperature-reads=" +
        $"{machine.PowerPorts.BatteryTemperatureAdcReadCount:n0}; " +
        $"bt-global-seen={indicators.BluetoothControllerStates.HasGlobalState}; " +
        $"bt-global=0x{indicators.BluetoothControllerStates.GlobalState:x2}; " +
        $"bt-global-count={indicators.BluetoothGlobalStateCommandCount:n0}; " +
        $"bt-link-mask=0x{indicators.BluetoothControllerStates.ObservedLinkSlotMask:x2}; " +
        $"bt-link-count={indicators.BluetoothLinkStateCommandCount:n0}; " +
        $"bt-records={indicators.BluetoothControllerRecordCount:n0}; " +
        $"bt-operation={indicators.BluetoothOperationRequestCount:n0}; " +
        $"bt-completion={indicators.BluetoothOperationCompletionCount:n0}; " +
        $"dsp-records={machine.BluetoothPeripheral?.ControllerRecordCount:n0}; " +
        $"dsp-commands={machine.BluetoothPeripheral?.DspCommandCount:n0}; " +
        $"peer-inquiry={machine.BluetoothPeripheral?.EmulatedInquiryResponseCount:n0}; " +
        $"peer-name={machine.BluetoothPeripheral?.EmulatedRemoteNameResponseCount:n0}; " +
        $"peer-pair-setup={machine.BluetoothPeripheral?.EmulatedPairingSetupResponseCount:n0}; " +
        $"peer-pair-records={machine.BluetoothPeripheral?.EmulatedPairingControllerRecordCount:n0}; " +
        $"peer-pair-complete={machine.BluetoothPeripheral?.EmulatedPairingCompletedCount:n0}; " +
        $"peer-pair-auth-fail=" +
        $"{machine.BluetoothPeripheral?.EmulatedPairingAuthenticationFailureCount:n0}; " +
        $"peer-connect-setup={machine.BluetoothPeripheral?.EmulatedConnectionSetupResponseCount:n0}; " +
        $"peer-connect-records={machine.BluetoothPeripheral?.EmulatedConnectionControllerRecordCount:n0}; " +
        $"peer-connect-complete={machine.BluetoothPeripheral?.EmulatedConnectionCompletedCount:n0}; " +
        $"peer-connect-auth-fail=" +
        $"{machine.BluetoothPeripheral?.EmulatedConnectionAuthenticationFailureCount:n0}; " +
        $"peer-sdp-search-attribute=" +
        $"{machine.BluetoothPeripheral?.EmulatedSdpServiceSearchAttributeRequestCount:n0}; " +
        $"peer-rfcomm-mux=" +
        $"{machine.BluetoothPeripheral?.EmulatedRfcommMultiplexerEstablishedCount:n0}; " +
        $"peer-rfcomm-opp=" +
        $"{machine.BluetoothPeripheral?.EmulatedRfcommObjectPushLinkEstablishedCount:n0}; " +
        $"peer-obex-connect=" +
        $"{machine.BluetoothPeripheral?.EmulatedObexConnectRequestCount:n0}; " +
        $"peer-obex-put=" +
        $"{machine.BluetoothPeripheral?.EmulatedObexPutRequestCount:n0}; " +
        $"peer-obex-final-put=" +
        $"{machine.BluetoothPeripheral?.EmulatedObexFinalPutRequestCount:n0}; " +
        $"peer-obex-disconnect=" +
        $"{machine.BluetoothPeripheral?.EmulatedObexDisconnectRequestCount:n0}; " +
        $"peer-obex-object-bytes=" +
        $"{machine.BluetoothPeripheral?.EmulatedObexTransferredObjectByteCount:n0}; " +
        $"peer-incoming-push-start=" +
        $"{machine.BluetoothPeripheral?.EmulatedIncomingObjectPushStartedCount:n0}; " +
        $"peer-incoming-push-complete=" +
        $"{machine.BluetoothPeripheral?.EmulatedIncomingObjectPushCompletedCount:n0}; " +
        $"peer-incoming-push-disconnect=" +
        $"{machine.BluetoothPeripheral?.EmulatedIncomingObjectPushDisconnectedCount:n0}; " +
        $"dsp-irq=0x{modemBus.IrqPending & ArmModemBus.DspInterruptBit:x5}; " +
        $"dsp-fifo={modemBus.DspInboundTransferByteCount}; " +
        $"dsp-state={Convert.ToHexString(modemBus.SnapshotExternal(0x01400c80, 0x24))}; " +
        $"bt-context={Convert.ToHexString(modemBus.SnapshotExternal(0x014210e0, 0x60))}; " +
        $"l2cap-channel={Convert.ToHexString(modemBus.SnapshotExternal(0x0142107c, 0x20))}; " +
        $"bt-producer={Convert.ToHexString(modemBus.SnapshotExternal(0x014215d0, 0x50))}; " +
        $"arm-stopped={machine.Modem!.IsStopped}; arm-sleep={machine.Modem.IsSleeping}; " +
        $"arm-stop-reason={machine.Modem.StopReason ?? "--"}; " +
        $"arm-irq={modemBus.IrqLineAsserted}; " +
        $"uart-tx={uartTransmitted:n0}; uart-rx={uartReceived:n0}");
}

static void PrintBytes(
    string title,
    Dictionary<string, List<byte>> captures)
{
    Console.WriteLine(title + ":");
    foreach (var pair in captures)
    {
        Console.WriteLine(
            $"  stage={pair.Key} count={pair.Value.Count:n0} " +
            $"bytes={Convert.ToHexString(pair.Value.ToArray())}");
    }
}

static void Print<TKey>(
    string title,
    Dictionary<TKey, long> histogram,
    Func<TKey, string> format)
    where TKey : notnull
{
    Console.WriteLine(title + ":");
    foreach (var pair in histogram.OrderBy(pair => format(pair.Key)))
    {
        Console.WriteLine($"  {format(pair.Key)} count={pair.Value:n0}");
    }
}

readonly record struct Contact(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask = null,
    bool Power = false);

sealed class L2capOutboundProbe(List<string> trace)
{
    public const byte InitialTransferKind = 6;
    public const byte ContinuationTransferKind = 5;
    const ushort SignalingChannelId = 0x0001;
    const ushort PeerRfcommChannelId = 0x0042;
    const ushort RfcommPsm = 0x0003;
    const byte LocalObjectPushServerChannel = 10;
    const byte PeerObjectPushDlci = LocalObjectPushServerChannel * 2 + 1;

    byte[]? _packet;
    int _packetLength;
    ushort? _handsetRfcommChannelId;
    bool _rfcommMultiplexerSabmObserved;
    bool _handsetOutboundFinalPutObserved;
    bool _handsetObjectPushPnResponseObserved;
    bool _handsetObjectPushMscObserved;
    bool _handsetObexConnectSuccessObserved;

    public void Observe(
        byte kind,
        ReadOnlySpan<byte> fragment,
        string stage,
        long cycle)
    {
        if (kind == InitialTransferKind)
        {
            _packet = null;
            _packetLength = 0;
            if (fragment.Length < 4)
            {
                return;
            }

            int packetLength =
                BinaryPrimitives.ReadUInt16LittleEndian(fragment) + 4;
            if (packetLength < 4 || fragment.Length > packetLength)
            {
                return;
            }
            _packet = new byte[packetLength];
        }
        else if (kind != ContinuationTransferKind ||
                 _packet is null ||
                 fragment.Length == 0 ||
                 fragment.Length > _packet.Length - _packetLength)
        {
            _packet = null;
            _packetLength = 0;
            return;
        }

        fragment.CopyTo(_packet.AsSpan(_packetLength));
        _packetLength += fragment.Length;
        if (_packetLength != _packet.Length)
        {
            return;
        }

        byte[] packet = _packet;
        _packet = null;
        _packetLength = 0;
        ObservePacket(packet, stage, cycle);
    }

    public bool TryCreatePeerObjectPushPn(out byte[] packet)
    {
        packet = [];
        if (_handsetRfcommChannelId is not { } handsetChannelId ||
            !_rfcommMultiplexerSabmObserved ||
            !_handsetOutboundFinalPutObserved)
        {
            return false;
        }

        ReadOnlySpan<byte> parameterNegotiation =
        [
            0x83, 0x11,
            PeerObjectPushDlci,
            0xf0,
            0x17,
            0x00,
            0xf6, 0x00,
            0x00,
            0x07,
        ];
        byte[] frame = CreatePeerRfcommUihFrame(0, parameterNegotiation);
        packet = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            handsetChannelId);
        frame.CopyTo(packet, 4);
        return true;
    }

    public bool TryCreatePeerObjectPushSabm(out byte[] packet)
    {
        packet = [];
        if (_handsetRfcommChannelId is not { } handsetChannelId ||
            !_handsetObjectPushPnResponseObserved)
        {
            return false;
        }

        Span<byte> frame = stackalloc byte[4];
        frame[0] = (byte)((PeerObjectPushDlci << 2) | 1);
        frame[1] = 0x3f;
        frame[2] = 0x01;
        frame[3] = CalculateRfcommFcs(frame[..3]);

        packet = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            handsetChannelId);
        frame.CopyTo(packet.AsSpan(4));
        return true;
    }

    public bool TryCreatePeerObexConnect(out byte[] packet)
    {
        packet = [];
        if (_handsetRfcommChannelId is not { } handsetChannelId ||
            !_handsetObjectPushMscObserved)
        {
            return false;
        }

        ReadOnlySpan<byte> obexConnect =
        [
            0x80, 0x00, 0x07,
            0x10, 0x00, 0x02, 0x00,
        ];
        byte[] frame = CreatePeerRfcommUihFrame(
            PeerObjectPushDlci,
            obexConnect);
        packet = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            handsetChannelId);
        frame.CopyTo(packet, 4);
        return true;
    }

    public bool TryCreatePeerObexFinalPut(out byte[] packet)
    {
        packet = [];
        if (_handsetRfcommChannelId is not { } handsetChannelId ||
            !_handsetObexConnectSuccessObserved)
        {
            return false;
        }

        ReadOnlySpan<byte> vCard =
            "BEGIN:VCARD\r\nVERSION:2.1\r\nN:;I\r\nTEL;CELL:456\r\nEND:VCARD\r\n"u8;
        byte[] obexPut = new byte[3 + 15 + 16 + 3 + vCard.Length];
        obexPut[0] = 0x82;
        BinaryPrimitives.WriteUInt16BigEndian(
            obexPut.AsSpan(1),
            checked((ushort)obexPut.Length));
        ReadOnlySpan<byte> nameHeader =
        [
            0x01, 0x00, 0x0f,
            0x00, (byte)'I',
            0x00, (byte)'.',
            0x00, (byte)'v',
            0x00, (byte)'c',
            0x00, (byte)'f',
            0x00, 0x00,
        ];
        nameHeader.CopyTo(obexPut.AsSpan(3));
        ReadOnlySpan<byte> typeHeader =
        [
            0x42, 0x00, 0x10,
            (byte)'T', (byte)'E', (byte)'X', (byte)'T',
            (byte)'/', (byte)'X', (byte)'-',
            (byte)'V', (byte)'C', (byte)'A', (byte)'R', (byte)'D',
            0x00,
        ];
        typeHeader.CopyTo(obexPut.AsSpan(3 + nameHeader.Length));
        int bodyHeaderOffset = 3 + nameHeader.Length + typeHeader.Length;
        obexPut[bodyHeaderOffset] = 0x49;
        BinaryPrimitives.WriteUInt16BigEndian(
            obexPut.AsSpan(bodyHeaderOffset + 1),
            checked((ushort)(vCard.Length + 3)));
        vCard.CopyTo(obexPut.AsSpan(bodyHeaderOffset + 3));

        byte[] frame = CreatePeerRfcommUihFrame(
            PeerObjectPushDlci,
            obexPut);
        packet = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            handsetChannelId);
        frame.CopyTo(packet, 4);
        return true;
    }

    public static IReadOnlyList<(byte Kind, byte[] Payload)> FragmentInbound(
        ReadOnlySpan<byte> packet)
    {
        var fragments = new List<(byte Kind, byte[] Payload)>();
        byte kind = InitialTransferKind;
        while (!packet.IsEmpty)
        {
            int length = Math.Min(17, packet.Length);
            fragments.Add((kind, packet[..length].ToArray()));
            packet = packet[length..];
            kind = ContinuationTransferKind;
        }
        return fragments;
    }

    void ObservePacket(
        ReadOnlySpan<byte> packet,
        string stage,
        long cycle)
    {
        ushort channelId =
            BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        if (channelId == SignalingChannelId)
        {
            ObserveSignaling(packet[4..], stage, cycle);
            return;
        }
        if (channelId != PeerRfcommChannelId || packet.Length < 8)
        {
            return;
        }

        byte dlci = (byte)(packet[4] >> 2);
        byte control = packet[5];
        if (dlci == 18 &&
            (control & 0xef) == 0xef)
        {
            int informationOffset = 7;
            if ((packet[6] & 1) == 0)
            {
                informationOffset++;
            }
            if ((control & 0x10) != 0)
            {
                informationOffset++;
            }
            if (informationOffset < packet.Length - 1 &&
                packet[informationOffset] == 0x82)
            {
                // Do not overlap the handset's client-side PN exchange with
                // the peer's server-side PN exchange. The live firmware can
                // carry both DLCs, but its RFCOMM PN transaction is singular;
                // begin the reverse push only after its final outbound PUT.
                _handsetOutboundFinalPutObserved = true;
            }
        }
        if (dlci == 0 &&
            (control & 0xef) == 0x2f &&
            (control & 0x10) != 0)
        {
            _rfcommMultiplexerSabmObserved = true;
        }
        if (dlci == 0 &&
            control == 0xef &&
            packet.Length >= 19 &&
            packet[8] == 0x81 &&
            packet[9] == 0x11 &&
            (packet[10] & 0x3f) == PeerObjectPushDlci)
        {
            _handsetObjectPushPnResponseObserved = true;
        }
        if (dlci == 0 &&
            control == 0xef &&
            packet.Length >= 13 &&
            packet[8..^1].SequenceEqual(
                new byte[] { 0xe3, 0x05, 0x57, 0x0d }))
        {
            _handsetObjectPushMscObserved = true;
        }
        if (dlci == PeerObjectPushDlci &&
            control == 0xef &&
            packet.Length >= 16 &&
            packet.Slice(8, 7).SequenceEqual(
                new byte[] { 0xa0, 0x00, 0x07, 0x10, 0x00, 0x02, 0x00 }))
        {
            _handsetObexConnectSuccessObserved = true;
        }
        if (dlci is 0 or PeerObjectPushDlci)
        {
            trace.Add(
                $"stage={stage} cycle={cycle:n0} handset-rfcomm " +
                $"cid=0x{channelId:x4} dlci={dlci} " +
                $"control=0x{control:x2} packet={Convert.ToHexString(packet)}");
        }
    }

    static byte[] CreatePeerRfcommUihFrame(
        byte dlci,
        ReadOnlySpan<byte> information)
    {
        var frame = new byte[4 + information.Length + 1];
        // 010c4a6c accepts UIH only when C/R equals the local MUX initiator
        // flag. The handset is the initiator here, so the peer uses C/R=1.
        frame[0] = (byte)((dlci << 2) | 3);
        frame[1] = 0xef;
        frame[2] = (byte)((information.Length << 1) & 0xfe);
        frame[3] = checked((byte)(information.Length >> 7));
        information.CopyTo(frame.AsSpan(4));
        frame[^1] = CalculateRfcommFcs(frame.AsSpan(0, 2));
        return frame;
    }

    void ObserveSignaling(
        ReadOnlySpan<byte> commands,
        string stage,
        long cycle)
    {
        while (commands.Length >= 4)
        {
            byte code = commands[0];
            byte identifier = commands[1];
            int commandLength =
                BinaryPrimitives.ReadUInt16LittleEndian(commands[2..]);
            if (commandLength > commands.Length - 4)
            {
                return;
            }

            ReadOnlySpan<byte> command = commands.Slice(4, commandLength);
            if (code == 0x02 &&
                command.Length == 4 &&
                BinaryPrimitives.ReadUInt16LittleEndian(command) == RfcommPsm)
            {
                _handsetRfcommChannelId =
                    BinaryPrimitives.ReadUInt16LittleEndian(command[2..]);
                trace.Add(
                    $"stage={stage} cycle={cycle:n0} handset-rfcomm-connect " +
                    $"identifier=0x{identifier:x2} " +
                    $"source-cid=0x{_handsetRfcommChannelId:x4}");
            }
            commands = commands[(4 + commandLength)..];
        }
    }

    static byte CalculateRfcommFcs(ReadOnlySpan<byte> bytes)
    {
        byte fcs = 0xff;
        foreach (byte next in bytes)
        {
            byte tableIndex = (byte)(fcs ^ next);
            for (var bit = 0; bit < 8; bit++)
            {
                tableIndex = (tableIndex & 1) != 0
                    ? (byte)((tableIndex >> 1) ^ 0xe0)
                    : (byte)(tableIndex >> 1);
            }
            fcs = tableIndex;
        }
        return (byte)(0xff - fcs);
    }
}

sealed class AvrSignalAndSwbpEndpointObserver(
    List<string> trace,
    ushort? tracedSignal,
    byte watchedEndpoint) : IMiaExecutionObserver
{
    const int ReceiveReturnPc = 0x001912;
    const int CurrentProcessIdAddress = 0x00f606;
    const int SwbpEndpointTableAddress = 0x02df0e;
    const int MaximumTraceEntries = 2_000;

    uint? _previousEntry;

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        int endpointAddress =
            SwbpEndpointTableAddress + watchedEndpoint * sizeof(uint);
        uint entry =
            (uint)cpu.ReadData(endpointAddress) |
            (uint)cpu.ReadData(endpointAddress + 1) << 8 |
            (uint)cpu.ReadData(endpointAddress + 2) << 16 |
            (uint)cpu.ReadData(endpointAddress + 3) << 24;
        if (_previousEntry != entry && trace.Count < MaximumTraceEntries)
        {
            trace.Add(
                $"swbp-entry i={machine.ExecutedInstructions:n0} " +
                $"pid=0x{cpu.ReadData(CurrentProcessIdAddress):x2} " +
                $"pc=0x{cpu.PC:x6} endpoint=0x{watchedEndpoint:x2} " +
                $"entry=0x{entry:x8} previous=" +
                (_previousEntry is { } previous
                    ? $"0x{previous:x8}"
                    : "unset"));
            _previousEntry = entry;
        }

        if (tracedSignal is not { } signal ||
            cpu.PC != ReceiveReturnPc ||
            trace.Count >= MaximumTraceEntries)
        {
            return null;
        }

        int logicalPayload =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        int payload = cpu.TranslateDataAddress(logicalPayload);
        if ((uint)payload <= (uint)(cpu.Data.Length - sizeof(ushort)) &&
            BitConverter.ToUInt16(cpu.Data, payload) == signal)
        {
            trace.Add(
                $"signal=0x{signal:x4} i={machine.ExecutedInstructions:n0} " +
                $"pid=0x{cpu.ReadData(CurrentProcessIdAddress):x2} " +
                $"logical=0x{logicalPayload:x6} physical=0x{payload:x6} " +
                $"bytes={Convert.ToHexString(cpu.Data.AsSpan(
                    payload,
                    Math.Min(128, cpu.Data.Length - payload)))}");
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

sealed class AvrBluetoothSignalObserver(
    List<string> trace,
    ushort tracedSignal) : IMiaExecutionObserver
{
    const int UiReceiveReturnPc = 0x001912;
    const int UiProcessIdAddress = 0x00f606;
    const int TraceInstructionCount = 5_000_000;

    int _remaining;
    int _discoveryScreen = -1;
    int _lastFrameVersion = -1;
    readonly HashSet<int> _seenPcs = [];
    readonly HashSet<string> _seenListEvents = [];
    readonly HashSet<int> _seenDiscoveryLifecycleCallbacks = [];
    readonly Dictionary<string, (int Address, byte[] Bytes)> _uiStateSnapshots = [];
    byte[]? _frameBeforeSignal;
    byte[]? _lastPublishedFrame;
    bool _discoveryCallbackPointersTraced;
    int _previousUiPc = -1;
    int _activeUiWriterCount;
    long _activeUiWriterEndInstruction = -1;

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        TraceActiveUiWriters(machine);
        if (cpu.PC is
                0x10e02d or
                0x10e03a or
                0x10e047 or
                0x10e072 or
                0x10e07e or
                0x10e0ca or
                0x10e12d or
                0x10dec0 &&
            _seenDiscoveryLifecycleCallbacks.Add(cpu.PC))
        {
            trace.Add(
                $"discovery-lifecycle-callback i={machine.ExecutedInstructions:n0} " +
                $"pc=0x{cpu.PC:x6} " +
                $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
                $"r28-31={Convert.ToHexString(cpu.Data.AsSpan(28, 4))} " +
                $"ramp={Convert.ToHexString(cpu.Data.AsSpan(0x58, 5))}");
            if (cpu.PC == 0x10dec0)
            {
                _uiStateSnapshots.Clear();
                _activeUiWriterCount = 0;
                _activeUiWriterEndInstruction =
                    machine.ExecutedInstructions + 3_000_000;
            }
        }
        if (cpu.PC is >= 0x10e0ca and <= 0x10e124)
        {
            trace.Add(
                $"discovery-key-handler i={machine.ExecutedInstructions:n0} " +
                $"pc=0x{cpu.PC:x6} " +
                $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
                $"selection={(_discoveryScreen >= 0
                    ? BitConverter.ToUInt16(cpu.Data, _discoveryScreen + 0x27)
                    : 0xffff):x4}");
        }
        if (cpu.PC == 0x07d850)
        {
            int logicalSignal = cpu.Data[16] |
                cpu.Data[17] << 8 |
                cpu.Data[18] << 16;
            int signal = cpu.TranslateDataAddress(logicalSignal);
            if ((uint)signal <=
                    (uint)(cpu.Data.Length - sizeof(ushort)) &&
                BitConverter.ToUInt16(cpu.Data, signal) == 0xd2fb)
            {
                int callback = cpu.Data[30] |
                    cpu.Data[31] << 8 |
                    cpu.Data[0x5c] << 16;
                int context = cpu.Data[20] |
                    cpu.Data[21] << 8 |
                    cpu.Data[22] << 16;
                trace.Add(
                    $"discovery-d2fb-callback context=0x{context:x6} " +
                    $"callback=0x{callback:x6}");
            }
        }
        if (cpu.PC == 0x07d850)
        {
            int logicalSignal = cpu.Data[16] |
                cpu.Data[17] << 8 |
                cpu.Data[18] << 16;
            int signal = cpu.TranslateDataAddress(logicalSignal);
            ushort signalId = (uint)signal <=
                (uint)(cpu.Data.Length - sizeof(ushort))
                ? BitConverter.ToUInt16(cpu.Data, signal)
                : (ushort)0xffff;
            int callback = cpu.Data[30] |
                cpu.Data[31] << 8 |
                cpu.Data[0x5c] << 16;
            int context = cpu.Data[20] |
                cpu.Data[21] << 8 |
                cpu.Data[22] << 16;
            if (signalId == tracedSignal)
            {
                _frameBeforeSignal = machine.Frame.ToArray();
                _lastPublishedFrame = _frameBeforeSignal;
                _lastFrameVersion = machine.FrameVersion;
                trace.Add(
                    $"signal-callback signal=0x{tracedSignal:x4} " +
                    $"logical-payload=0x{logicalSignal:x6} " +
                    $"payload=0x{signal:x6} context=0x{context:x6} " +
                    $"callback=0x{callback:x6} bytes=" +
                    Convert.ToHexString(
                        cpu.Data.AsSpan(
                            signal,
                            Math.Min(32, cpu.Data.Length - signal))));
                _seenPcs.Clear();
                _seenListEvents.Clear();
                _remaining = TraceInstructionCount;
            }
            if (callback is >= 0x10dec0 and < 0x10e199)
            {
                if (_discoveryScreen != context)
                {
                    _discoveryScreen = context;
                    _uiStateSnapshots.Clear();
                }
            }
            if (context != _discoveryScreen)
            {
                _previousUiPc = cpu.PC;
                return null;
            }
            trace.Add(
                $"discovery-screen-callback signal=0x{signalId:x4} " +
                $"pointer=0x{signal:x6} context=0x{context:x6} " +
                $"callback=0x{callback:x6} " +
                $"progress={cpu.Data[context + 0x53]} " +
                $"state2a={cpu.Data[context + 0x2a]:x2} " +
                $"state2b={cpu.Data[context + 0x2b]:x2} " +
                $"selection={BitConverter.ToUInt16(cpu.Data, context + 0x27):x4}");
            if (signalId == 0xd2fa &&
                (uint)signal <= (uint)(cpu.Data.Length - 0xa4))
            {
                trace.Add(
                    $"discovery-d2fa-signal={Convert.ToHexString(
                        cpu.Data.AsSpan(signal, 0xa4))}");
            }
            if (signalId == 0xd2ff && !_discoveryCallbackPointersTraced)
            {
                _discoveryCallbackPointersTraced = true;
                TraceDiscoveryCallbackPointers(cpu.Data);
            }
        }
        if (cpu.PC == UiReceiveReturnPc)
        {
            int processId = cpu.ReadData(UiProcessIdAddress);
            int logicalPayload =
                cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
            int payload = cpu.TranslateDataAddress(logicalPayload);
            if ((uint)payload < (uint)(cpu.Data.Length - 2))
            {
                ushort signalId = BitConverter.ToUInt16(cpu.Data, payload);
                if (signalId == 0xd2fb && processId == 0x63)
                {
                    trace.Add(
                        $"discovery-ui-receive signal=0x{signalId:x4} " +
                        $"logical-payload=0x{logicalPayload:x6} " +
                        $"payload=0x{payload:x6} bytes=" +
                        Convert.ToHexString(
                            cpu.Data.AsSpan(
                                payload,
                                Math.Min(32, cpu.Data.Length - payload))));
                    _seenPcs.Clear();
                    _remaining = TraceInstructionCount;
                }
                if (signalId == tracedSignal)
                {
                    _frameBeforeSignal = machine.Frame.ToArray();
                    _lastPublishedFrame = _frameBeforeSignal;
                    _lastFrameVersion = machine.FrameVersion;
                    trace.Add(
                        $"signal=0x{tracedSignal:x4} pid=0x{processId:x2} " +
                        $"logical-payload=0x{logicalPayload:x6} " +
                        $"payload=0x{payload:x6} bytes=" +
                        Convert.ToHexString(
                            cpu.Data.AsSpan(
                                payload,
                                Math.Min(32, cpu.Data.Length - payload))));
                    _seenPcs.Clear();
                    _seenListEvents.Clear();
                    _remaining = TraceInstructionCount;
                }
            }
        }

        if (_remaining > 0)
        {
            TraceDiscoveryListAccess(machine);
        }

        bool bluetoothUiPc = cpu.PC is >= 0x047000 and < 0x047900;
        bool discoveryScreenPc =
            cpu.PC is >= 0x10dec0 and < 0x10e00f or
            >= 0x10e12d and < 0x10e199;
        bool eventDispatchPc = cpu.PC is
            0x07d4f4 or
            0x07d51a or
            0x07d51b or
            0x07d523 or
            0x07d568 or
            0x07d596 or
            0x07d59d or
            0x07d7af or
            0x07d850 or
            0x07d983 or
            0x07d9b5;
        if (_remaining-- > 0 &&
            (bluetoothUiPc ||
             discoveryScreenPc ||
             eventDispatchPc ||
             _seenPcs.Add(cpu.PC)))
        {
            trace.Add(
                $"i={machine.ExecutedInstructions:n0} c={machine.Cycles:n0} " +
                $"pc=0x{cpu.PC:x6} pid=0x{cpu.ReadData(UiProcessIdAddress):x2} " +
                $"sp=0x{cpu.SP:x4} " +
                $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
                $"r28-31={Convert.ToHexString(cpu.Data.AsSpan(28, 4))} " +
                $"ramp={Convert.ToHexString(cpu.Data.AsSpan(0x58, 5))}");
            if (cpu.PC is 0x10e00d or 0x10e195 &&
                _discoveryScreen >= 0)
            {
                if (_frameBeforeSignal is not null)
                {
                    trace.Add(DescribeFrameDelta(
                        _frameBeforeSignal,
                        machine.Frame.Span));
                }
                trace.Add(
                    $"discovery-screen=0x{_discoveryScreen:x6}:" +
                    Convert.ToHexString(
                        cpu.Data.AsSpan(_discoveryScreen, 0x200)));
                int itemStorage = cpu.Data[_discoveryScreen + 0x24] |
                    cpu.Data[_discoveryScreen + 0x25] << 8 |
                    cpu.Data[_discoveryScreen + 0x26] << 16;
                if ((uint)itemStorage <=
                    (uint)(cpu.Data.Length - 0x200))
                {
                    trace.Add(
                        $"discovery-item-storage=0x{itemStorage:x6}:" +
                        Convert.ToHexString(
                            cpu.Data.AsSpan(itemStorage, 0x200)));
                    int itemPointers = cpu.Data[itemStorage + 4] |
                        cpu.Data[itemStorage + 5] << 8 |
                        cpu.Data[itemStorage + 6] << 16;
                    if ((uint)itemPointers <=
                        (uint)(cpu.Data.Length - 0x40))
                    {
                        trace.Add(
                            $"discovery-item-pointers=0x{itemPointers:x6}:" +
                            Convert.ToHexString(
                                cpu.Data.AsSpan(itemPointers, 0x40)));
                        int firstItem = cpu.Data[itemPointers] |
                            cpu.Data[itemPointers + 1] << 8 |
                            cpu.Data[itemPointers + 2] << 16;
                        if ((uint)firstItem <=
                            (uint)(cpu.Data.Length - 0x100))
                        {
                            trace.Add(
                                $"discovery-first-item=0x{firstItem:x6}:" +
                                Convert.ToHexString(
                                    cpu.Data.AsSpan(firstItem, 0x100)));
                        }
                    }
                }
                trace.Add(
                    $"discovery-device={Convert.ToHexString(
                        cpu.Data.AsSpan(0x011291, 0x60))}");
                trace.Add(
                    $"discovery-child-389a3={Convert.ToHexString(
                        cpu.Data.AsSpan(0x0389a3, 0x80))}");
                trace.Add(
                    $"discovery-child-3893d={Convert.ToHexString(
                        cpu.Data.AsSpan(0x03893d, 0x66))}");
                trace.Add(
                    $"discovery-child-3c897={Convert.ToHexString(
                        cpu.Data.AsSpan(0x03c897, 0x80))}");
                if (cpu.PC == 0x10e195)
                {
                    var nameReferences = new List<string>();
                    for (var address = 0; address <= cpu.Data.Length - 3; address++)
                    {
                        if (cpu.Data[address] == 0x91 &&
                            cpu.Data[address + 1] == 0x12 &&
                            cpu.Data[address + 2] == 0x01)
                        {
                            int start = Math.Max(0, address - 16);
                            int length = Math.Min(
                                48,
                                cpu.Data.Length - start);
                            nameReferences.Add(
                                $"0x{address:x6}:" +
                                Convert.ToHexString(
                                    cpu.Data.AsSpan(start, length)));
                        }
                    }
                    trace.Add(
                        $"discovery-name-references={string.Join(
                            ",",
                            nameReferences.Take(100))}");
                    trace.Add(
                        $"discovery-name-wrapper={Convert.ToHexString(
                            cpu.Data.AsSpan(0x0370c0, 0xc0))}");
                    var wrapperReferences = new List<string>();
                    for (var address = 0; address <= cpu.Data.Length - 3; address++)
                    {
                        if (cpu.Data[address] == 0x0b &&
                            cpu.Data[address + 1] == 0x71 &&
                            cpu.Data[address + 2] == 0x03)
                        {
                            int start = Math.Max(0, address - 24);
                            int length = Math.Min(
                                64,
                                cpu.Data.Length - start);
                            wrapperReferences.Add(
                                $"0x{address:x6}:" +
                                Convert.ToHexString(
                                    cpu.Data.AsSpan(start, length)));
                        }
                    }
                    trace.Add(
                        $"discovery-wrapper-references={string.Join(
                            ",",
                            wrapperReferences.Take(100))}");
                }
            }
            if (cpu.PC == 0x07d850 &&
                _discoveryScreen >= 0 &&
                ReadPointer(cpu.Data, 20, 21, 22) == _discoveryScreen)
            {
                trace.Add(
                    $"discovery-dispatch-registers={Convert.ToHexString(
                        cpu.Data.AsSpan(0, 32))}");
                int x = cpu.Data[26] |
                    cpu.Data[27] << 8 |
                    cpu.Data[0x59] << 16;
                int savedPointer = cpu.Data[8] |
                    cpu.Data[9] << 8 |
                    cpu.Data[10] << 16;
                int lowSavedPointer = cpu.Data[4] |
                    cpu.Data[5] << 8 |
                    cpu.Data[6] << 16;
                foreach (var (label, pointer) in new[]
                         {
                             ("x", x),
                             ("r8", savedPointer),
                             ("r4", lowSavedPointer),
                         })
                {
                    if ((uint)pointer < (uint)cpu.Data.Length)
                    {
                        int start = Math.Max(0, pointer - 32);
                        int length = Math.Min(
                            128,
                            cpu.Data.Length - start);
                        trace.Add(
                            $"discovery-dispatch-{label}=0x{pointer:x6}:" +
                            Convert.ToHexString(
                                cpu.Data.AsSpan(start, length)));
                    }
                }
                foreach (var (label, start, length) in new[]
                         {
                             ("screen-nodes", 0x03c7e0, 0x180),
                             ("screen-model", 0x0402c0, 0xc0),
                             ("screen-index", 0x041040, 0x100),
                             ("screen-owner", 0x050130, 0x120),
                         })
                {
                    trace.Add(
                        $"discovery-dispatch-{label}=0x{start:x6}:" +
                        Convert.ToHexString(
                            cpu.Data.AsSpan(start, length)));
                }
                var screenReferences = new List<string>();
                for (var address = 0; address <= cpu.Data.Length - 3; address++)
                {
                    if (cpu.Data[address] == (byte)_discoveryScreen &&
                        cpu.Data[address + 1] == (byte)(_discoveryScreen >> 8) &&
                        cpu.Data[address + 2] == (byte)(_discoveryScreen >> 16))
                    {
                        int start = Math.Max(0, address - 32);
                        int length = Math.Min(
                            96,
                            cpu.Data.Length - start);
                        screenReferences.Add(
                            $"0x{address:x6}:" +
                            Convert.ToHexString(
                                cpu.Data.AsSpan(start, length)));
                    }
                }
                trace.Add(
                    $"discovery-screen-references={string.Join(
                        ",",
                        screenReferences.Take(100))}");
            }
        }
        _previousUiPc = cpu.PC;
        return null;
    }

    void TraceActiveUiWriters(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (machine.ExecutedInstructions > _activeUiWriterEndInstruction ||
            _discoveryScreen < 0 ||
            (uint)_discoveryScreen > (uint)(cpu.Data.Length - 0x60))
        {
            _previousUiPc = cpu.PC;
            return;
        }

        var regions = new List<(string Label, int Address, int Length)>
        {
            ("screen", _discoveryScreen, 0x60),
        };
        foreach (int offset in new[] { 0x15, 0x18, 0x1b })
        {
            int child = ReadPointer(
                cpu.Data,
                _discoveryScreen + offset,
                _discoveryScreen + offset + 1,
                _discoveryScreen + offset + 2);
            if (child >= 0x100 && (uint)child <= (uint)(cpu.Data.Length - 0x60))
            {
                regions.Add(($"child-{offset:x2}", child, 0x60));
            }
        }

        foreach (var region in regions)
        {
            byte[] current = cpu.Data
                .AsSpan(region.Address, region.Length)
                .ToArray();
            if (!_uiStateSnapshots.TryGetValue(region.Label, out var previous) ||
                previous.Address != region.Address)
            {
                _uiStateSnapshots[region.Label] = (region.Address, current);
                trace.Add(
                    $"active-ui-region label={region.Label} " +
                    $"address=0x{region.Address:x6} bytes={Convert.ToHexString(current)}");
                continue;
            }

            var changes = new List<string>();
            for (var offset = 0; offset < current.Length; offset++)
            {
                if (current[offset] != previous.Bytes[offset])
                {
                    changes.Add(
                        $"+0x{offset:x2}:{previous.Bytes[offset]:x2}>{current[offset]:x2}");
                }
            }
            if (changes.Count != 0 && _activeUiWriterCount++ < 2_000)
            {
                trace.Add(
                    $"active-ui-writer i={machine.ExecutedInstructions:n0} " +
                    $"previous-pc=0x{_previousUiPc:x6} current-pc=0x{cpu.PC:x6} " +
                    $"label={region.Label} address=0x{region.Address:x6} " +
                    $"changes={string.Join(",", changes)} " +
                    $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
                    $"r28-31={Convert.ToHexString(cpu.Data.AsSpan(28, 4))} " +
                    $"ramp={Convert.ToHexString(cpu.Data.AsSpan(0x58, 5))}");
            }
            _uiStateSnapshots[region.Label] = (region.Address, current);
        }
        _previousUiPc = cpu.PC;
    }

    void TraceDiscoveryListAccess(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (machine.FrameVersion != _lastFrameVersion)
        {
            byte[] frame = machine.Frame.ToArray();
            trace.Add(
                $"discovery-frame-published i={machine.ExecutedInstructions:n0} " +
                $"pc=0x{cpu.PC:x6} version={machine.FrameVersion} " +
                DescribeFrameDelta(
                    _lastPublishedFrame ?? frame,
                    frame));
            _lastPublishedFrame = frame;
            _lastFrameVersion = machine.FrameVersion;
        }

        if (cpu.PC is 0x079e0e or 0x079e33 or
            0x0b4bd9 or 0x0b4be0 or 0x0b4be6 or
            0x0b5283 or 0x0b5285 or 0x0b5291 or 0x0b5295)
        {
            string key =
                $"list-access:{cpu.PC:x6}:" +
                Convert.ToHexString(cpu.Data.AsSpan(16, 10)) + ":" +
                Convert.ToHexString(cpu.Data.AsSpan(28, 4));
            if (_seenListEvents.Add(key))
            {
                trace.Add(
                    $"discovery-list-access i={machine.ExecutedInstructions:n0} " +
                    $"pc=0x{cpu.PC:x6} " +
                    $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
                    $"r28-31={Convert.ToHexString(cpu.Data.AsSpan(28, 4))} " +
                    $"ramp={Convert.ToHexString(cpu.Data.AsSpan(0x58, 5))}");
            }
        }

        int item = GetFirstDiscoveryItem(cpu.Data, _discoveryScreen);
        if (item < 0)
        {
            return;
        }

        foreach (var (label, pointer) in new[]
                 {
                     ("r4", ReadPointer(cpu.Data, 4, 5, 6)),
                     ("r8", ReadPointer(cpu.Data, 8, 9, 10)),
                     ("r16", ReadPointer(cpu.Data, 16, 17, 18)),
                     ("r20", ReadPointer(cpu.Data, 20, 21, 22)),
                     ("x", ReadPointer(cpu.Data, 26, 27, 0x59)),
                     ("y", ReadPointer(cpu.Data, 28, 29, 0x5a)),
                     ("z", ReadPointer(cpu.Data, 30, 31, 0x5b)),
                 })
        {
            bool itemPointer = pointer >= item && pointer < item + 0x93;
            bool namePointer = IsPeerNamePointer(cpu.Data, pointer);
            if (!itemPointer && !namePointer)
            {
                continue;
            }

            string key =
                $"pointer:{cpu.PC:x6}:{label}:{pointer:x6}";
            if (_seenListEvents.Add(key))
            {
                trace.Add(
                    $"discovery-item-use i={machine.ExecutedInstructions:n0} " +
                    $"pc=0x{cpu.PC:x6} {label}=0x{pointer:x6} " +
                    $"item=0x{item:x6} name={namePointer} " +
                    $"r16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))} " +
                    $"r28-31={Convert.ToHexString(cpu.Data.AsSpan(28, 4))} " +
                    $"ramp={Convert.ToHexString(cpu.Data.AsSpan(0x58, 5))}");
            }
        }

        if (cpu.PC == 0x10e0f7 &&
            _seenListEvents.Add("row-string-object"))
        {
            int rowString = ReadPointer(cpu.Data, 20, 21, 22);
            TraceMemory(cpu.Data, "discovery-row-string", rowString, 0x100);
            int rowStringTarget = ReadPointer(
                cpu.Data,
                rowString + 4,
                rowString + 5,
                rowString + 6);
            TraceMemory(
                cpu.Data,
                "discovery-row-string-target",
                rowStringTarget,
                0x180);
            var peerCopies = new List<string>();
            ReadOnlySpan<byte> peerName = "Mia Peer"u8;
            for (var address = 0;
                 address <= cpu.Data.Length - peerName.Length;
                 address++)
            {
                if (cpu.Data.AsSpan(address, peerName.Length)
                    .SequenceEqual(peerName))
                {
                    int start = Math.Max(0, address - 16);
                    int length = Math.Min(64, cpu.Data.Length - start);
                    peerCopies.Add(
                        $"0x{address:x6}:" +
                        Convert.ToHexString(
                            cpu.Data.AsSpan(start, length)));
                }
            }
            trace.Add(
                $"discovery-peer-copies={string.Join(",", peerCopies)}");
        }
        if (cpu.PC == 0x0b47e8 &&
            _seenListEvents.Add("row-native-object"))
        {
            int rowObject = ReadPointer(cpu.Data, 16, 17, 18);
            TraceMemory(cpu.Data, "discovery-row-object", rowObject, 0x180);
        }
        if (cpu.PC == 0x10e104 &&
            _seenListEvents.Add("screen-children-after-row"))
        {
            TraceMemory(
                cpu.Data,
                "discovery-screen-after-row",
                _discoveryScreen,
                0x180);
            foreach (int offset in new[] { 0x15, 0x18, 0x1b })
            {
                int child = ReadPointer(
                    cpu.Data,
                    _discoveryScreen + offset,
                    _discoveryScreen + offset + 1,
                    _discoveryScreen + offset + 2);
                TraceMemory(
                    cpu.Data,
                    $"discovery-screen-child-{offset:x2}",
                    child,
                    0x180);
            }
        }
    }

    void TraceMemory(byte[] data, string label, int pointer, int length)
    {
        if ((uint)pointer > (uint)(data.Length - length))
        {
            trace.Add($"{label}=0x{pointer:x6}:invalid");
            return;
        }
        trace.Add(
            $"{label}=0x{pointer:x6}:" +
            Convert.ToHexString(data.AsSpan(pointer, length)));
    }

    void TraceDiscoveryCallbackPointers(byte[] data)
    {
        foreach (int callback in new[]
                 {
                     0x10dec0,
                     0x10e02d,
                     0x10e03a,
                     0x10e047,
                     0x10e072,
                     0x10e07e,
                     0x10e0ca,
                     0x10e12d,
                 })
        {
            var matches = new List<string>();
            for (var address = 0; address <= data.Length - 3; address++)
            {
                if (data[address] != (byte)callback ||
                    data[address + 1] != (byte)(callback >> 8) ||
                    data[address + 2] != (byte)(callback >> 16))
                {
                    continue;
                }

                int start = Math.Max(0, address - 32);
                int length = Math.Min(96, data.Length - start);
                matches.Add(
                    $"0x{address:x6}:" +
                    Convert.ToHexString(data.AsSpan(start, length)));
            }
            trace.Add(
                $"discovery-callback-pointers callback=0x{callback:x6} " +
                $"matches={string.Join(",", matches.Take(100))}");
        }
    }

    static int GetFirstDiscoveryItem(byte[] data, int screen)
    {
        if (screen < 0 || (uint)screen > (uint)(data.Length - 0x57))
        {
            return -1;
        }
        int descriptor = ReadPointer(data, screen + 0x24, screen + 0x25, screen + 0x26);
        if (descriptor < 0x100 || (uint)descriptor > (uint)(data.Length - 7))
        {
            return -1;
        }
        int pointers = ReadPointer(data, descriptor + 4, descriptor + 5, descriptor + 6);
        if (pointers < 0x100 || (uint)pointers > (uint)(data.Length - 3))
        {
            return -1;
        }
        int item = ReadPointer(data, pointers, pointers + 1, pointers + 2);
        return item >= 0x100 && (uint)item <= (uint)(data.Length - 0x93)
            ? item
            : -1;
    }

    static int ReadPointer(byte[] data, int low, int high, int bank) =>
        data[low] | data[high] << 8 | data[bank] << 16;

    static bool IsPeerNamePointer(byte[] data, int pointer)
    {
        ReadOnlySpan<byte> peerName = "Mia Peer"u8;
        if ((uint)pointer > (uint)(data.Length - peerName.Length))
        {
            return false;
        }
        if (data.AsSpan(pointer, peerName.Length).SequenceEqual(peerName))
        {
            return true;
        }
        return pointer < data.Length - peerName.Length - 1 &&
            data[pointer] == peerName.Length &&
            data.AsSpan(pointer + 1, peerName.Length).SequenceEqual(peerName);
    }

    static string DescribeFrameDelta(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after)
    {
        var changed = 0;
        var minX = 101;
        var minY = 80;
        var maxX = -1;
        var maxY = -1;
        for (var offset = 0; offset < Math.Min(before.Length, after.Length); offset++)
        {
            if (before[offset] == after[offset])
            {
                continue;
            }

            changed++;
            int x = offset % 101;
            int y = offset / 101;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        return $"discovery-frame-delta changed={changed} " +
            $"bounds={minX},{minY}..{maxX},{maxY} " +
            $"before={Convert.ToHexStringLower(SHA256.HashData(before))} " +
            $"after={Convert.ToHexStringLower(SHA256.HashData(after))}";
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}

static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary,
        TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = new TValue();
            dictionary.Add(key, value);
        }
        return value;
    }
}

sealed class NativeLinkFrameDecoder
{
    const int MaximumPayloadLength = 4096;

    readonly List<byte> _payload = [];
    int _state;
    int _length;
    byte _destination;
    byte _source;

    public NativeLinkFrame? Push(byte value)
    {
        switch (_state)
        {
            case 0:
                _state = value == 0xab ? 1 : 0;
                break;
            case 1:
                _state = value switch { 0xba => 2, 0xab => 1, _ => 0 };
                break;
            case 2:
                _destination = value;
                _state = 3;
                break;
            case 3:
                _length = value;
                _state = 4;
                break;
            case 4:
                _length |= value << 8;
                if (_length > MaximumPayloadLength)
                {
                    Reset(value);
                }
                else
                {
                    _state = 5;
                }
                break;
            case 5:
                _source = value;
                _payload.Clear();
                if (_length == 0)
                {
                    return Complete();
                }
                _state = 6;
                break;
            case 6:
                _payload.Add(value);
                if (_payload.Count == _length)
                {
                    return Complete();
                }
                break;
        }
        return null;
    }

    NativeLinkFrame Complete()
    {
        var frame = new NativeLinkFrame(_destination, _source, _payload.ToArray());
        Reset();
        return frame;
    }

    void Reset(byte possibleSync = 0)
    {
        _state = possibleSync == 0xab ? 1 : 0;
        _length = 0;
        _destination = 0;
        _source = 0;
        _payload.Clear();
    }
}

readonly record struct NativeLinkFrame(byte Destination, byte Source, byte[] Payload);

// ARM-to-ASIC UART frames do not include the AVR-to-ARM AB BA sync bytes.
// The first byte is the destination with bit 7 set, followed by LE length,
// ARM source, and payload. This decoder is deliberately separate so captures
// cannot silently reinterpret one direction as the other.
sealed class AsicLinkFrameDecoder
{
    const int MaximumPayloadLength = 4096;

    readonly List<byte> _payload = [];
    int _state;
    int _length;
    byte _destination;
    byte _source;

    public NativeLinkFrame? Push(byte value)
    {
        switch (_state)
        {
            case 0:
                if ((value & 0x80) != 0)
                {
                    _destination = (byte)(value & 0x7f);
                    _state = 1;
                }
                break;
            case 1:
                _length = value;
                _state = 2;
                break;
            case 2:
                _length |= value << 8;
                if (_length > MaximumPayloadLength)
                {
                    Reset();
                }
                else
                {
                    _state = 3;
                }
                break;
            case 3:
                _source = value;
                _payload.Clear();
                _state = _length == 0 ? 0 : 4;
                if (_length == 0)
                {
                    return new NativeLinkFrame(_destination, _source, []);
                }
                break;
            case 4:
                _payload.Add(value);
                if (_payload.Count == _length)
                {
                    var frame = new NativeLinkFrame(
                        _destination,
                        _source,
                        _payload.ToArray());
                    Reset();
                    return frame;
                }
                break;
        }
        return null;
    }

    void Reset()
    {
        _state = 0;
        _length = 0;
        _destination = 0;
        _source = 0;
        _payload.Clear();
    }
}
