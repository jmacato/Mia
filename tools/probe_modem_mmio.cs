#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Arm7Core;

if (args.Length is < 1 or > 2 || !long.TryParse(args[0], out long instructionLimit))
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/probe_modem_mmio.cs -- INSTRUCTIONS [GDFS.raw]");
    return 2;
}

string gdfsPath = args.Length == 2
    ? args[1]
    : "images/T68i_Full_GDFS.compact.raw";
byte measurement = Environment.GetEnvironmentVariable("MIA_DSP_MEASUREMENT") is { } text
    ? Convert.ToByte(text, 16)
    : (byte)0xd0;
byte reportSlot = Environment.GetEnvironmentVariable("MIA_DSP_REPORT_SLOT") is { } slotText
    ? Convert.ToByte(slotText, 16)
    : (byte)0;
byte frameStatus = Environment.GetEnvironmentVariable("MIA_DSP_FRAME_STATUS") is { } statusText
    ? Convert.ToByte(statusText, 16)
    : (byte)0x10;
byte[]? oneShotRawResponse = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_RESPONSE") is { } rawResponseText
    ? Convert.FromHexString(rawResponseText)
    : null;
byte oneShotRawKind = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_KIND") is { } rawKindText
    ? Convert.ToByte(rawKindText, 16)
    : (byte)3;
byte oneShotRawControl = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_CONTROL") is { } rawControlText
    ? Convert.ToByte(rawControlText, 16)
    : (byte)0;
byte? oneShotRawStatus = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_STATUS") is { } rawStatusText
    ? Convert.ToByte(rawStatusText, 16)
    : null;
byte[][] oneShotRawSequence = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_SEQUENCE") is { } rawSequenceText
    ? rawSequenceText.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(Convert.FromHexString)
        .ToArray()
    : [];
byte[]? oneShotRawTriggerPacket = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_TRIGGER_PACKET") is { } rawTriggerPacketText
    ? Convert.FromHexString(rawTriggerPacketText)
    : null;
List<RawResponseRule> rawResponseRules = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_RULES") is { } rawRulesText
    ? ParseRawResponseRules(rawRulesText)
    : [];
bool suppressDefaultDspResponse = Environment.GetEnvironmentVariable(
    "MIA_DSP_NO_DEFAULT_RESPONSE") == "1";
bool fillExperimentalFramesWithKind3 = Environment.GetEnvironmentVariable(
    "MIA_PROBE_FILL_FRAMES_WITH_KIND3") == "1";
bool repeatRawResponse = Environment.GetEnvironmentVariable(
    "MIA_DSP_RAW_REPEAT") == "1";
bool oneShotRawResponseSent = false;
bool acknowledgeDspTransfers = Environment.GetEnvironmentVariable(
    "MIA_DSP_ACK_TRANSFERS") == "1";
bool traceLinkManagerStateChanges = Environment.GetEnvironmentVariable(
    "MIA_TRACE_LM_STATE_CHANGES") == "1";
bool traceLlGpioSetPin = Environment.GetEnvironmentVariable(
    "MIA_TRACE_LLGPIO_SETPIN") == "1";
bool rewriteScanRequestAsAcquisition = Environment.GetEnvironmentVariable(
    "MIA_PROBE_D2D4") == "1";
bool gateExperimentalFramesOnD2d4 = Environment.GetEnvironmentVariable(
    "MIA_PROBE_GATE_FRAMES_ON_D2D4") == "1";
bool acquisitionProbeActive = false;
bool shortenC353Timer = Environment.GetEnvironmentVariable(
    "MIA_PROBE_SHORTEN_C353_TIMER") == "1";
byte? probeC356Byte10 = Environment.GetEnvironmentVariable(
    "MIA_PROBE_C356_BYTE10") is { } probeC356Byte10Text
    ? Convert.ToByte(probeC356Byte10Text, 16)
    : null;
byte? probeC3ffResult = Environment.GetEnvironmentVariable(
    "MIA_PROBE_C3FF") is { } probeC3ffResultText
    ? Convert.ToByte(probeC3ffResultText, 16)
    : null;
byte[]? probeState9DspRecord = Environment.GetEnvironmentVariable(
    "MIA_PROBE_STATE9_DSP_RECORD") is { } probeState9DspRecordText
    ? Convert.FromHexString(probeState9DspRecordText)
    : null;
byte probeDspRecordState = Environment.GetEnvironmentVariable(
    "MIA_PROBE_DSP_RECORD_STATE") is { } probeDspRecordStateText
    ? Convert.ToByte(probeDspRecordStateText, 16)
    : (byte)9;
long? probeDspRecordCycle = Environment.GetEnvironmentVariable(
    "MIA_PROBE_DSP_RECORD_CYCLE") is { } probeDspRecordCycleText
    ? Convert.ToInt64(probeDspRecordCycleText)
    : null;
byte? probeDspRecordFrameState = Environment.GetEnvironmentVariable(
    "MIA_PROBE_DSP_RECORD_FRAME_STATE") is { } probeDspRecordFrameStateText
    ? Convert.ToByte(probeDspRecordFrameStateText, 16)
    : null;
if (probeState9DspRecord is not null && probeState9DspRecord.Length != 0x11)
{
    throw new ArgumentException(
        "MIA_PROBE_STATE9_DSP_RECORD must be one 17-byte kind-3 record");
}
bool probeState9DspRecordSent = false;
byte[]? probeState10DspRecord = Environment.GetEnvironmentVariable(
    "MIA_PROBE_STATE10_DSP_RECORD") is { } probeState10DspRecordText
    ? Convert.FromHexString(probeState10DspRecordText)
    : null;
if (probeState10DspRecord is not null && probeState10DspRecord.Length != 0x11)
{
    throw new ArgumentException(
        "MIA_PROBE_STATE10_DSP_RECORD must be one 17-byte kind-3 record");
}
bool probeState10DspRecordSent = false;
byte[]? probeState15DspRecord = Environment.GetEnvironmentVariable(
    "MIA_PROBE_STATE15_DSP_RECORD") is { } probeState15DspRecordText
    ? Convert.FromHexString(probeState15DspRecordText)
    : null;
if (probeState15DspRecord is not null && probeState15DspRecord.Length != 0x11)
{
    throw new ArgumentException(
        "MIA_PROBE_STATE15_DSP_RECORD must be one 17-byte kind-3 record");
}
bool probeState15DspRecordSent = false;
byte[]? probeState16DspRecord = Environment.GetEnvironmentVariable(
    "MIA_PROBE_STATE16_DSP_RECORD") is { } probeState16DspRecordText
    ? Convert.FromHexString(probeState16DspRecordText)
    : null;
if (probeState16DspRecord is not null && probeState16DspRecord.Length != 0x11)
{
    throw new ArgumentException(
        "MIA_PROBE_STATE16_DSP_RECORD must be one 17-byte kind-3 record");
}
bool probeState16DspRecordSent = false;
HashSet<byte> acknowledgedDspPacketTypes = Environment.GetEnvironmentVariable(
    "MIA_DSP_ACK_PACKET_TYPES") is { } ackPacketTypesText
    ? ackPacketTypesText
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(text => Convert.ToByte(text, 16))
        .ToHashSet()
    : [];
byte? tracedDspStatus = Environment.GetEnvironmentVariable(
    "MIA_TRACE_DSP_STATUS") is { } tracedDspStatusText
    ? Convert.ToByte(tracedDspStatusText, 16)
    : null;
byte[]? tracedDspStatusAfterPacket = Environment.GetEnvironmentVariable(
    "MIA_TRACE_DSP_STATUS_AFTER_PACKET") is { } tracedDspStatusAfterPacketText
    ? Convert.FromHexString(tracedDspStatusAfterPacketText)
    : null;
(ushort Start, ushort End)? fakeAdminReadRange = Environment.GetEnvironmentVariable(
    "MIA_FAKE_ADMIN_READ_RANGE") is { } fakeAdminReadRangeText
    ? ParseRange(fakeAdminReadRangeText)
    : null;
byte[] fakeAdminReadPayload = Environment.GetEnvironmentVariable(
    "MIA_FAKE_ADMIN_READ_PAYLOAD") is { } fakeAdminReadPayloadText
    ? Convert.FromHexString(fakeAdminReadPayloadText)
    : new byte[0x16];
if (fakeAdminReadRange.HasValue && fakeAdminReadPayload.Length != 0x16)
{
    throw new ArgumentException("MIA_FAKE_ADMIN_READ_PAYLOAD must be one 22-byte scan record");
}
byte[] modemFirmware = File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih");
byte[] modemPayload = File.Exists("/private/tmp/mia-modem-payload.bin")
    ? File.ReadAllBytes("/private/tmp/mia-modem-payload.bin")
    : modemFirmware;
using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes(gdfsPath),
    modemFirmware,
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
const int experimentalDspFrameCycles = 60_000;
for (var address = 0; address <= byte.MaxValue; address++)
{
    machine.Modem!.Bus.SetDspIndexedRegister((byte)address, 0);
}
machine.Modem!.Bus.SetDspIndexedRegister(15, measurement);
machine.Modem.Bus.SetDspIndexedRegister(16, measurement);
machine.Modem.Bus.SetDspIndexedRegister(14, reportSlot);
long nextExperimentalDspFrameCycle = long.MaxValue;
int experimentalDspFrameNumber = 0;
var scheduledDspStatuses = new PriorityQueue<byte, long>();

var accesses = new Dictionary<AccessKey, AccessSummary>();
var dspPortWrites = new List<ArmModemMmioAccess>();
var dspPortAccesses = new List<ArmModemMmioAccess>();
var linkManagerActions = new List<LinkManagerAction>();
var enqueuedDspCommands = new List<EnqueuedDspCommand>();
var oseSignalSends = new List<OseSignalSend>();
var driverVariableEvents = new List<DriverVariableEvent>();
var driverPathEvents = new List<DriverPathEvent>();
var dspFifoCopyEvents = new List<DspFifoCopyEvent>();
var dspVariableCompleteEvents = new List<DspVariableCompleteEvent>();
var dspVariableFreeEvents = new List<DspVariableFreeEvent>();
var oseSignalFreeEvents = new List<OseSignalFreeEvent>();
var linkManagerActionReturns = new List<LinkManagerActionReturn>();
var controlSignalProducerEvents = new List<ControlSignalProducerEvent>();
var oseSignalReceiveEvents = new List<OseSignalReceiveEvent>();
var adminReadHelperEvents = new List<AdminReadHelperEvent>();
var linkManagerStateChanges = new List<LinkManagerStateChange>();
var decodedDspRecords = new List<DecodedDspRecord>();
var linkManagerStateMappings = new List<LinkManagerStateMapping>();
var physicalOutputEvents = new List<string>();
var llGpioSetPinCalls = new List<(long Cycle, uint Pin, uint State, uint Return)>();
var llGpioConfigureCalls = new List<(long Cycle, uint Pin, uint Direction, uint Return)>();
ushort? tracedAction = Environment.GetEnvironmentVariable("MIA_TRACE_ACTION") is { } actionText
    ? Convert.ToUInt16(actionText, 16)
    : null;
int tracedActionOccurrence = Environment.GetEnvironmentVariable(
    "MIA_TRACE_ACTION_OCCURRENCE") is { } actionOccurrenceText
    ? Convert.ToInt32(actionOccurrenceText)
    : 1;
ushort? tracedReceiveSignal = Environment.GetEnvironmentVariable(
    "MIA_TRACE_RECEIVE_SIGNAL") is { } receiveSignalText
    ? Convert.ToUInt16(receiveSignalText, 16)
    : null;
int tracedReceiveInstructionBudget = Environment.GetEnvironmentVariable(
    "MIA_TRACE_RECEIVE_INSTRUCTIONS") is { } receiveInstructionBudgetText
    ? Convert.ToInt32(receiveInstructionBudgetText)
    : 4_000;
var tracedActionInstructions = new List<uint>();
var tracedActionRegisters = new List<ActionRegisterSnapshot>();
var tracedReceiveInstructions = new List<uint>();
var tracedReceiveRegisters = new List<DspStatusRegisterSnapshot>();
int tracedReceiveInstructionsRemaining = 0;
var tracedDspStatusInstructions = new List<uint>();
var tracedDspStatusRegisters = new List<DspStatusRegisterSnapshot>();
long? tracedDspStatusCycle = null;
int tracedDspStatusInstructionsRemaining = 0;
bool tracedDspStatusArmed = tracedDspStatusAfterPacket is null;
var outboundDspPacket = new List<byte>();
var modemDebugBytes = new List<byte>();
ushort? activeLinkManagerAction = null;
int tracedActionSeen = 0;
bool traceActiveLinkManagerAction = false;
uint linkManagerActionReturn = 0;
ushort activeLinkManagerSignal = 0xffff;
uint activeLinkManagerState = 0xffff;
ushort? pendingAdminReadId = null;
ushort? pendingFakeAdminReadId = null;
var fakeAdminReadPayloadInjections = new List<FakeAdminReadPayloadInjection>();
uint previousInstructionPc = 0;
uint previousLinkManagerState = BitConverter.ToUInt32(
    machine.Modem.Bus.SnapshotExternal(0x01400c3c, sizeof(uint)));
byte[] activeDecodedDspRecord = [];
int enlargedD2d5AllocationCount = 0;
byte[] d2d4AllocationHeader = [];
machine.PowerPorts.OutputStateChanged += state => physicalOutputEvents.Add(
    $"cycle={machine.Cycles:n0} power A3={state.PortA3:x2} A4={state.PortA4:x2} " +
    $"A5={state.PortA5:x2} A6={state.PortA6:x2} A7={state.PortA7:x2} " +
    $"A9={state.PortA9:x2} AA={state.PowerControl:x2} B0={state.PortB0:x2}");
machine.SecondaryPorts.OutputStateChanged += state => physicalOutputEvents.Add(
    $"cycle={machine.Cycles:n0} secondary 40={state.Register40:x2} " +
    $"48={state.Register48:x2} 80={state.Register80:x2}");
machine.SecondaryPorts.OpaqueRegisterWritten += (register, value) =>
    physicalOutputEvents.Add(
        $"cycle={machine.Cycles:n0} secondary opaque {register:x2}={value:x2}");
machine.Modem.InstructionExecuting += modem =>
{
    if (traceLlGpioSetPin && modem.CurrentInstructionAddress == 0x010e1f3c)
    {
        llGpioSetPinCalls.Add((
            modem.Cycles,
            modem.Cpu.GetGpr(0) & 0xff,
            modem.Cpu.GetGpr(1) & 0xff,
            modem.Cpu.GetGpr(14) & ~1u));
    }
    if (traceLlGpioSetPin && modem.CurrentInstructionAddress == 0x010e1a46)
    {
        llGpioConfigureCalls.Add((
            modem.Cycles,
            modem.Cpu.GetGpr(0) & 0xff,
            modem.Cpu.GetGpr(1) & 0xff,
            modem.Cpu.GetGpr(14) & ~1u));
    }
    if (probeDspRecordCycle.HasValue && probeState9DspRecord is not null &&
        !probeState9DspRecordSent && modem.Cycles >= probeDspRecordCycle.Value)
    {
        // Explicitly temporary timing probe at the real DSP FIFO/IRQ boundary.
        modem.Bus.QueueDspInboundTransfer(0, 3, probeState9DspRecord);
        probeState9DspRecordSent = true;
    }
    while (modem.Cycles >= nextExperimentalDspFrameCycle)
    {
        int frame = experimentalDspFrameNumber++;
        byte low = (byte)frame;
        byte middle = (byte)(frame >> 8);
        byte high = (byte)(frame >> 16);
        modem.Bus.SetDspIndexedRegister(4, (byte)(low << 2));
        modem.Bus.SetDspIndexedRegister(5, (byte)((low >> 6) | (middle << 2)));
        modem.Bus.SetDspIndexedRegister(6, (byte)((middle >> 6) | (high << 2)));
        modem.Bus.SetDspIndexedRegister(7, (byte)(high >> 6));
        if ((frameStatus & 0x10) != 0)
        {
            uint frameState = BitConverter.ToUInt32(
                modem.Bus.SnapshotExternal(0x01400c3c, sizeof(uint)));
            bool gateFrame = gateExperimentalFramesOnD2d4 && acquisitionProbeActive &&
                (probeDspRecordFrameState != frameState || probeState9DspRecordSent) &&
                !(frameState == 10 && probeState9DspRecordSent &&
                    probeState10DspRecord is not null && !probeState10DspRecordSent) &&
                !(frameState == 15 && probeState10DspRecordSent &&
                    probeState15DspRecord is not null && !probeState15DspRecordSent) &&
                !(frameState == 16 && probeState15DspRecordSent &&
                    probeState16DspRecord is not null && !probeState16DspRecordSent);
            if (gateExperimentalFramesOnD2d4 && acquisitionProbeActive &&
                frameState == 8 && probeDspRecordFrameState == 9 &&
                !probeState9DspRecordSent)
            {
                // Keep the inspected measurement/report path running until its
                // C3E5 action advances the acquisition state from 8 to 9.
                modem.Bus.QueueDspInboundPayload([0x0c, measurement, measurement]);
            }
            else if (gateFrame)
            {
                // Let pre-acquisition transfers drain, then stop after the one
                // candidate frame so no synthetic completion is introduced.
            }
            else if (frameState == 10 && probeState9DspRecordSent &&
                probeState10DspRecord is not null && !probeState10DspRecordSent)
            {
                // Explicitly temporary continuation of the inspected acquisition
                // protocol through the same DSP FIFO and IRQ boundary.
                modem.Bus.QueueDspInboundTransfer(0, 3, probeState10DspRecord);
                probeState10DspRecordSent = true;
            }
            else if (frameState == 15 && probeState10DspRecordSent &&
                probeState15DspRecord is not null && !probeState15DspRecordSent)
            {
                // Explicitly temporary continuation of the inspected multistage
                // acquisition protocol through the DSP FIFO and IRQ boundary.
                modem.Bus.QueueDspInboundTransfer(0, 3, probeState15DspRecord);
                probeState15DspRecordSent = true;
            }
            else if (frameState == 16 && probeState15DspRecordSent &&
                probeState16DspRecord is not null && !probeState16DspRecordSent)
            {
                // Explicitly temporary continuation after inspecting state 10's
                // C3FF receiver and action 0034 completion path.
                modem.Bus.QueueDspInboundTransfer(0, 3, probeState16DspRecord);
                probeState16DspRecordSent = true;
            }
            else if (probeDspRecordFrameState == frameState &&
                probeState9DspRecord is not null && !probeState9DspRecordSent)
            {
                // Explicitly temporary protocol experiment: use the inspected
                // kind-3 record as this report frame instead of an empty kind 0.
                modem.Bus.QueueDspInboundTransfer(0, 3, probeState9DspRecord);
                probeState9DspRecordSent = true;
            }
            else
            {
                if (fillExperimentalFramesWithKind3)
                {
                    // Explicitly temporary protocol experiment: keep the FIFO
                    // populated with the already decoded measurement record.
                    modem.Bus.QueueDspInboundPayload([0x0c, measurement, measurement]);
                }
                else
                {
                    // Explicitly preserve the experiment's old empty kind-0 frame.
                    // Production transport fails closed instead of inventing it.
                    modem.Bus.QueueDspInboundTransfer(0, 0, []);
                }
            }
        }
        byte remainingStatus = (byte)(frameStatus & ~0x10);
        if (remainingStatus != 0)
        {
            modem.Bus.AssertDspStatus(remainingStatus);
        }
        nextExperimentalDspFrameCycle += experimentalDspFrameCycles;
    }
    while (scheduledDspStatuses.TryPeek(out byte status, out long cycle) &&
        cycle <= modem.Cycles)
    {
        scheduledDspStatuses.Dequeue();
        modem.Bus.AssertDspStatus(status);
    }

    uint pc = modem.CurrentInstructionAddress;
    if (shortenC353Timer && pc == 0x01000418 &&
        modem.Cpu.GetGpr(1) == 0xc353)
    {
        // Explicitly temporary timing probe after inspecting action 001B's
        // real C353 timer setup. The timer still allocates and delivers C353.
        modem.Cpu.SetGpr(0, 1);
    }
    if (rewriteScanRequestAsAcquisition && pc == 0x01001ac8 &&
        modem.Cpu.GetGpr(1) == 0xd2d5)
    {
        // Explicitly temporary ABI probe: request the inspected D2D4 size from
        // the real OSE allocator before repurposing the pending D2D5 signal.
        modem.Cpu.SetGpr(0, 0x19);
        enlargedD2d5AllocationCount++;
    }
    if (pc == 0x010c1a68 && linkManagerStateMappings.Count == 0)
    {
        uint descriptorAddress = modem.Cpu.GetGpr(7);
        byte[] descriptor = SnapshotArmMemory(modem, modemPayload, descriptorAddress, 0x18);
        if (descriptor.Length == 0x18)
        {
            uint dispatcher = BitConverter.ToUInt32(descriptor, 8) & ~1u;
            uint transitionTable = BitConverter.ToUInt32(descriptor, 12);
            uint stateRanges = BitConverter.ToUInt32(descriptor, 16);
            if (dispatcher == 0x010adb72)
            {
                for (int fsmState = 1; fsmState <= 0x40; fsmState++)
                {
                    byte[] rangeBytes = SnapshotArmMemory(
                        modem,
                        modemPayload,
                        stateRanges + (uint)((fsmState - 1) * sizeof(uint)),
                        sizeof(uint) * 2);
                    if (rangeBytes.Length != sizeof(uint) * 2)
                    {
                        break;
                    }
                    int start = (short)BitConverter.ToUInt32(rangeBytes, 0);
                    int end = (short)BitConverter.ToUInt32(rangeBytes, sizeof(uint));
                    if (start < 0 || end < start || end > 5_000)
                    {
                        continue;
                    }
                    byte[] transitions = SnapshotArmMemory(
                        modem,
                        modemPayload,
                        transitionTable + (uint)(start * sizeof(uint)),
                        (end - start) * sizeof(uint));
                    for (int index = start; index < end && transitions.Length >= 4; index++)
                    {
                        int offset = (index - start) * sizeof(uint);
                        linkManagerStateMappings.Add(new(
                            fsmState,
                            index,
                            BitConverter.ToUInt16(transitions, offset),
                            BitConverter.ToUInt16(transitions, offset + 2)));
                    }
                }
            }
        }
    }
    if (pc == 0x010bbd9a)
    {
        uint recordAddress = modem.Cpu.GetGpr(2);
        activeDecodedDspRecord = recordAddress < ArmModemBus.InternalRamSize - 0x11
            ? modem.Bus.SnapshotInternal(recordAddress, 0x11)
            : recordAddress is >= ArmModemBus.ExternalRamBase and
                < ArmModemBus.ExternalRamBase +
                    ArmModemBus.ExternalRamMirrorSpan - 0x11
                ? modem.Bus.SnapshotExternal(recordAddress, 0x11)
                : [];
    }
    else if (pc == 0x010c1ad4 &&
        modem.Cpu.GetGpr(14) is >= 0x010bbd00 and < 0x010bc900 &&
        decodedDspRecords.Count < 1_000)
    {
        uint decodedSignalAddress = modem.Cpu.GetGpr(0);
        byte[] decodedSignalBytes = decodedSignalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(decodedSignalAddress, 32)
            : [];
        decodedDspRecords.Add(new(
            modem.Cycles,
            activeDecodedDspRecord,
            decodedSignalAddress,
            decodedSignalBytes.Length >= 2
                ? BitConverter.ToUInt16(decodedSignalBytes, 0)
                : (ushort)0xffff,
            decodedSignalBytes));
    }
    if (traceLinkManagerStateChanges)
    {
        uint currentState = BitConverter.ToUInt32(
            modem.Bus.SnapshotExternal(0x01400c3c, sizeof(uint)));
        if (currentState != previousLinkManagerState &&
            linkManagerStateChanges.Count < 1_000)
        {
            linkManagerStateChanges.Add(new(
                modem.Cycles,
                previousInstructionPc,
                pc,
                previousLinkManagerState,
                currentState,
                activeLinkManagerAction,
                activeLinkManagerSignal,
                modem.Cpu.GetGpr(0),
                modem.Cpu.GetGpr(1),
                modem.Cpu.GetGpr(2),
                modem.Cpu.GetGpr(3),
                modem.Cpu.GetGpr(4),
                modem.Cpu.GetGpr(14)));
            if (!probeDspRecordCycle.HasValue && !probeDspRecordFrameState.HasValue &&
                currentState == probeDspRecordState && probeState9DspRecord is not null &&
                !probeState9DspRecordSent)
            {
                // Explicitly temporary protocol experiment: inject one inspected
                // kind-3 record through the firmware-visible DSP FIFO and IRQ.
                modem.Bus.QueueDspInboundTransfer(0, 3, probeState9DspRecord);
                probeState9DspRecordSent = true;
            }
            previousLinkManagerState = currentState;
        }
        previousInstructionPc = pc;
    }
    if (tracedReceiveInstructionsRemaining > 0)
    {
        tracedReceiveInstructions.Add(pc);
        tracedReceiveRegisters.Add(new(
            pc,
            modem.Cpu.GetGpr(0),
            modem.Cpu.GetGpr(1),
            modem.Cpu.GetGpr(2),
            modem.Cpu.GetGpr(3),
            modem.Cpu.GetGpr(4),
            modem.Cpu.GetGpr(5),
            modem.Cpu.GetGpr(6),
            modem.Cpu.GetGpr(7),
            modem.Cpu.GetGpr(8),
            modem.Cpu.GetGpr(9),
            modem.Cpu.GetGpr(10),
            modem.Cpu.GetGpr(11),
            modem.Cpu.GetGpr(12),
            modem.Cpu.GetGpr(13),
            modem.Cpu.GetGpr(14)));
        tracedReceiveInstructionsRemaining--;
    }
    if (pc == 0x010ac74c && adminReadHelperEvents.Count < 1_000)
    {
        adminReadHelperEvents.Add(new(
            modem.Cycles,
            (ushort)modem.Cpu.GetGpr(1),
            modem.Cpu.GetGpr(0),
            modem.Cpu.GetGpr(14) & ~1u,
            IsReturn: false,
            []));
    }
    else if (pc == 0x010ac77e && adminReadHelperEvents.Count < 1_000)
    {
        uint destination = modem.Cpu.GetGpr(4) - 0x1c;
        byte[] result = destination is >= ArmModemBus.ExternalRamBase and
            < ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan - 0x1c
                ? modem.Bus.SnapshotExternal(destination, 0x1c)
                : destination < ArmModemBus.InternalRamSize - 0x1c
                    ? modem.Bus.SnapshotInternal(destination, 0x1c)
                    : [];
        adminReadHelperEvents.Add(new(
            modem.Cycles,
            (ushort)modem.Cpu.GetGpr(5),
            destination,
            modem.Cpu.GetGpr(14) & ~1u,
            IsReturn: true,
            result));
    }
    if ((pc is 0x010a7350 or 0x010a73b8 or 0x010a73f0 or 0x010a7430 or
        0x010a7478 or 0x010a74ac or 0x010a74e8 or 0x010a7538 or
        0x010a756c or 0x010a75ac or 0x010a761c or 0x010a7654 or
        0x010a7678 or 0x010a76d0 or 0x010a770c) &&
        controlSignalProducerEvents.Count < 1_000)
    {
        ushort producedSignal = pc switch
        {
            0x010a7350 => 0xc356,
            0x010a73b8 => 0xc38b,
            0x010a73f0 => 0xc38e,
            0x010a7430 => 0xc358,
            0x010a7478 => 0xc35c,
            0x010a74ac => 0xc35d,
            0x010a74e8 => 0xc364,
            0x010a7538 => 0xc365,
            0x010a756c => 0xc367,
            0x010a75ac => 0xc36b,
            0x010a761c => 0xc36e,
            0x010a7654 => 0xc370,
            0x010a7678 => 0xc3ad,
            0x010a76d0 => 0xc3b6,
            0x010a770c => 0xc3b0,
            _ => 0xffff,
        };
        controlSignalProducerEvents.Add(new(
            modem.Cycles,
            pc,
            producedSignal,
            modem.Cpu.GetGpr(0),
            modem.Cpu.GetGpr(1),
            modem.Cpu.GetGpr(2),
            modem.Cpu.GetGpr(3),
            modem.Cpu.GetGpr(14)));
    }
    if (tracedDspStatusInstructionsRemaining > 0)
    {
        tracedDspStatusInstructions.Add(pc);
        tracedDspStatusRegisters.Add(new(
            pc,
            modem.Cpu.GetGpr(0),
            modem.Cpu.GetGpr(1),
            modem.Cpu.GetGpr(2),
            modem.Cpu.GetGpr(3),
            modem.Cpu.GetGpr(4),
            modem.Cpu.GetGpr(5),
            modem.Cpu.GetGpr(6),
            modem.Cpu.GetGpr(7),
            modem.Cpu.GetGpr(8),
            modem.Cpu.GetGpr(9),
            modem.Cpu.GetGpr(10),
            modem.Cpu.GetGpr(11),
            modem.Cpu.GetGpr(12),
            modem.Cpu.GetGpr(13),
            modem.Cpu.GetGpr(14)));
        tracedDspStatusInstructionsRemaining--;
    }
    if (traceActiveLinkManagerAction &&
        tracedActionInstructions.Count < 100_000)
    {
        tracedActionInstructions.Add(pc);
        if (tracedActionRegisters.Count < 2_000)
        {
            tracedActionRegisters.Add(new(
                pc,
                modem.Cpu.GetGpr(0),
                modem.Cpu.GetGpr(1),
                modem.Cpu.GetGpr(2),
                modem.Cpu.GetGpr(3),
                modem.Cpu.GetGpr(4),
                modem.Cpu.GetGpr(7)));
        }
    }
    if (pc == 0x010be0cc && driverVariableEvents.Count < 100)
    {
        uint sp = modem.Cpu.GetGpr(13);
        uint driverSignalAddress = sp + 0x10 < ArmModemBus.InternalRamSize - 4
            ? BitConverter.ToUInt32(modem.Bus.SnapshotInternal(sp + 0x10, 4))
            : 0;
        byte[] driverSignal = driverSignalAddress < ArmModemBus.InternalRamSize - 24
            ? modem.Bus.SnapshotInternal(driverSignalAddress, 24)
            : [];
        byte driverState = modem.Bus.SnapshotExternal(0x01400c96, 1)[0];
        driverVariableEvents.Add(new(modem.Cycles, driverSignalAddress, driverState, driverSignal));
    }
    if ((pc is 0x010c0312 or 0x010c1038) && driverPathEvents.Count < 100)
    {
        driverPathEvents.Add(new(
            modem.Cycles,
            pc,
            modem.Bus.SnapshotExternal(0x01400c40, 0x60)));
    }
    if ((pc is 0x010c17b8 or 0x010c17e2 or 0x010c17fa or 0x010c1828 or
        0x010c1840) && dspVariableCompleteEvents.Count < 200)
    {
        uint variableSignalAddress = BitConverter.ToUInt32(
            modem.Bus.SnapshotExternal(0x01400c8c, sizeof(uint)));
        byte[] variableSignal = variableSignalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(variableSignalAddress, 32)
            : [];
        dspVariableCompleteEvents.Add(new(
            modem.Cycles,
            pc,
            modem.Cpu.GetGpr(0),
            modem.Cpu.GetGpr(1),
            modem.Cpu.GetGpr(2),
            modem.Cpu.GetGpr(14),
            variableSignalAddress,
            variableSignal,
            modem.Bus.SnapshotExternal(0x01400c40, 0x60)));
    }
    if ((pc is 0x01001c74 or 0x01001c7a or 0x01001c86) &&
        dspVariableFreeEvents.Count < 200)
    {
        uint slotAddress = modem.Cpu.GetGpr(0);
        if (oseSignalFreeEvents.Count < 500)
        {
            byte[]? pointerBytes = slotAddress < ArmModemBus.InternalRamSize - sizeof(uint)
                ? modem.Bus.SnapshotInternal(slotAddress, sizeof(uint))
                : slotAddress is >= ArmModemBus.ExternalRamBase and
                    < ArmModemBus.ExternalRamBase +
                        ArmModemBus.ExternalRamMirrorSpan - sizeof(uint)
                    ? modem.Bus.SnapshotExternal(slotAddress, sizeof(uint))
                    : null;
            if (pointerBytes is not null)
            {
                uint freeSignalAddress = BitConverter.ToUInt32(pointerBytes);
                byte[]? freeSignal = freeSignalAddress < ArmModemBus.InternalRamSize - 32
                    ? modem.Bus.SnapshotInternal(freeSignalAddress, 32)
                    : freeSignalAddress is >= ArmModemBus.ExternalRamBase and
                        < ArmModemBus.ExternalRamBase +
                            ArmModemBus.ExternalRamMirrorSpan - 32
                        ? modem.Bus.SnapshotExternal(freeSignalAddress, 32)
                        : null;
                if (freeSignal is not null)
                {
                    ushort freeSignalId = BitConverter.ToUInt16(freeSignal, 0);
                    if (freeSignalId is 0xc3e5 or 0xc3e6 or 0xc3f2 or 0xc418 or 0xc419)
                    {
                        uint routerDescriptor = modem.Cpu.GetGpr(7);
                        uint routerSignalTable = 0;
                        if (routerDescriptor < ArmModemBus.InternalRamSize - 0x10)
                        {
                            routerSignalTable = BitConverter.ToUInt32(
                                modem.Bus.SnapshotInternal(routerDescriptor + 0xc, sizeof(uint)));
                        }
                        else if (routerDescriptor >= ArmModemBus.ExternalRamBase &&
                            routerDescriptor <
                                ArmModemBus.ExternalRamBase +
                                ArmModemBus.ExternalRamMirrorSpan - 0x10)
                        {
                            routerSignalTable = BitConverter.ToUInt32(
                                modem.Bus.SnapshotExternal(routerDescriptor + 0xc, sizeof(uint)));
                        }
                        else if (routerDescriptor >= ArmModemFlash.BaseAddress &&
                            routerDescriptor - ArmModemFlash.BaseAddress <
                                modemPayload.Length - 0x10)
                        {
                            routerSignalTable = BitConverter.ToUInt32(
                                modemPayload.AsSpan(
                                    (int)(routerDescriptor - ArmModemFlash.BaseAddress + 0xc),
                                    sizeof(uint)));
                        }
                        oseSignalFreeEvents.Add(new(
                            modem.Cycles,
                            pc,
                            slotAddress,
                            freeSignalAddress,
                            modem.Cpu.GetGpr(14),
                            routerDescriptor,
                            routerSignalTable,
                            modem.Cpu.GetGpr(4),
                            freeSignal));
                    }
                }
            }
        }
        if (slotAddress is >= 0x01421828 and < 0x01421850)
        {
            uint freedSignalAddress = BitConverter.ToUInt32(
                modem.Bus.SnapshotExternal(slotAddress, sizeof(uint)));
            byte[] freedSignal = freedSignalAddress < ArmModemBus.InternalRamSize - 32
                ? modem.Bus.SnapshotInternal(freedSignalAddress, 32)
                : [];
            dspVariableFreeEvents.Add(new(
                modem.Cycles,
                pc,
                slotAddress,
                freedSignalAddress,
                modem.Cpu.GetGpr(14),
                freedSignal,
                modem.Bus.SnapshotExternal(0x01400c60, 0x20)));
        }
    }
    if ((pc is 0x01192478 or 0x0119247e or 0x010c18e8 or 0x010c194a) &&
        dspFifoCopyEvents.Count < 200)
    {
        uint r0 = modem.Cpu.GetGpr(0);
        uint r1 = modem.Cpu.GetGpr(1);
        uint r2 = modem.Cpu.GetGpr(2);
        if (r1 == 0x00800810 || pc is 0x010c18e8 or 0x010c194a)
        {
            byte[] destination = r0 < ArmModemBus.InternalRamSize - 16
                ? modem.Bus.SnapshotInternal(r0, 16)
                : r0 is >= ArmModemBus.ExternalRamBase and
                    < ArmModemBus.ExternalRamBase +
                        ArmModemBus.ExternalRamMirrorSpan - 16
                    ? modem.Bus.SnapshotExternal(r0, 16)
                    : [];
            dspFifoCopyEvents.Add(new(
                modem.Cycles,
                pc,
                r0,
                r1,
                r2,
                modem.Cpu.GetGpr(3),
                modem.Cpu.GetGpr(14),
                destination));
        }
    }
    if (activeLinkManagerAction.HasValue && pc == linkManagerActionReturn)
    {
        if (linkManagerActionReturns.Count < 20_000)
        {
            linkManagerActionReturns.Add(new(
                modem.Cycles,
                activeLinkManagerAction.Value,
                activeLinkManagerState,
                activeLinkManagerSignal,
                modem.Cpu.GetGpr(0)));
        }
        traceActiveLinkManagerAction = false;
        activeLinkManagerAction = null;
    }

    if (pendingFakeAdminReadId is { } fakeAdminReadId && pc == 0x010ac772)
    {
        uint stackPointer = modem.Cpu.GetGpr(13);
        for (var index = 0; index < fakeAdminReadPayload.Length; index++)
        {
            modem.Bus.WriteByte(
                stackPointer + 4u + (uint)index,
                fakeAdminReadPayload[index],
                ArmAccess.None);
        }
        modem.Bus.WriteByte(stackPointer + 26u, 0, ArmAccess.None);
        modem.Bus.WriteByte(stackPointer + 27u, 0, ArmAccess.None);
        if (fakeAdminReadPayloadInjections.Count < 1_000)
        {
            fakeAdminReadPayloadInjections.Add(new(
                modem.Cycles,
                pc,
                fakeAdminReadId,
                stackPointer,
                fakeAdminReadPayload.ToArray()));
        }
        pendingFakeAdminReadId = null;
    }

    if ((pc is 0x01001e7c or 0x01001e94 or 0x01001dda) &&
        oseSignalReceiveEvents.Count < 20_000)
    {
        uint receivedSignalAddress = modem.Cpu.GetGpr(0);
        byte[]? receivedBytes = receivedSignalAddress < ArmModemBus.InternalRamSize - 32
            ? modem.Bus.SnapshotInternal(receivedSignalAddress, 32)
            : receivedSignalAddress is >= ArmModemBus.ExternalRamBase and
                < ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan - 32
                ? modem.Bus.SnapshotExternal(receivedSignalAddress, 32)
                : null;
        ushort receivedSignal = receivedBytes is not null
            ? BitConverter.ToUInt16(receivedBytes, 0)
            : (ushort)0xffff;
        if (rewriteScanRequestAsAcquisition && receivedSignal == 0xd2d5 &&
            receivedSignalAddress < ArmModemBus.InternalRamSize - 0x19)
        {
            // Explicitly temporary experiment: reuse the fixed-size OSE pool block
            // to exercise the inspected D2D4 receive path. Never a production ABI.
            d2d4AllocationHeader = receivedSignalAddress >= 8
                ? modem.Bus.SnapshotInternal(receivedSignalAddress - 8, 8)
                : [];
            modem.Bus.WriteByte(receivedSignalAddress, 0xd4, ArmAccess.None);
            for (uint offset = 2; offset < 0x19; offset++)
            {
                modem.Bus.WriteByte(
                    receivedSignalAddress + offset,
                    0,
                    ArmAccess.None);
            }
            receivedBytes = modem.Bus.SnapshotInternal(receivedSignalAddress, 32);
            receivedSignal = 0xd2d4;
            acquisitionProbeActive = true;
        }
        if (probeC3ffResult.HasValue && receivedSignal == 0xc3e6 &&
            receivedSignalAddress < ArmModemBus.InternalRamSize - 4 &&
            BitConverter.ToUInt32(
                modem.Bus.SnapshotExternal(0x01400c3c, sizeof(uint))) == 9)
        {
            // Explicitly temporary state-machine probe after inspecting state 9.
            modem.Bus.WriteByte(receivedSignalAddress, 0xff, ArmAccess.None);
            modem.Bus.WriteByte(receivedSignalAddress + 1, 0xc3, ArmAccess.None);
            modem.Bus.WriteByte(
                receivedSignalAddress + 3,
                probeC3ffResult.Value,
                ArmAccess.None);
            receivedBytes = modem.Bus.SnapshotInternal(receivedSignalAddress, 32);
            receivedSignal = 0xc3ff;
        }
        if (receivedSignal == tracedReceiveSignal &&
            tracedReceiveInstructions.Count == 0)
        {
            tracedReceiveInstructionsRemaining = tracedReceiveInstructionBudget;
        }
        oseSignalReceiveEvents.Add(new(
            modem.Cycles,
            pc,
            activeLinkManagerAction,
            receivedSignalAddress,
            receivedSignal,
            modem.Cpu.GetGpr(14),
            receivedBytes ?? []));
    }

    if (pc == 0x01003040 && oseSignalSends.Count < 20_000)
    {
        uint pointerAddress = modem.Cpu.GetGpr(0);
        byte[]? pointerBytes = pointerAddress < ArmModemBus.InternalRamSize - sizeof(uint)
            ? modem.Bus.SnapshotInternal(pointerAddress, sizeof(uint))
            : pointerAddress is >= ArmModemBus.ExternalRamBase and
                < ArmModemBus.ExternalRamBase +
                    ArmModemBus.ExternalRamMirrorSpan - sizeof(uint)
                ? modem.Bus.SnapshotExternal(pointerAddress, sizeof(uint))
                : null;
        if (pointerBytes is not null)
        {
            uint sentSignalAddress = BitConverter.ToUInt32(pointerBytes);
            byte[]? bytes = sentSignalAddress < ArmModemBus.InternalRamSize - 32
                ? modem.Bus.SnapshotInternal(sentSignalAddress, 32)
                : sentSignalAddress is >= ArmModemBus.ExternalRamBase and
                    < ArmModemBus.ExternalRamBase + ArmModemBus.ExternalRamMirrorSpan - 32
                    ? modem.Bus.SnapshotExternal(sentSignalAddress, 32)
                    : null;
            if (bytes is not null)
            {
                ushort sentSignalSize = sentSignalAddress >= 6 &&
                    sentSignalAddress - 6 < ArmModemBus.InternalRamSize - sizeof(ushort)
                    ? BitConverter.ToUInt16(
                        modem.Bus.SnapshotInternal(sentSignalAddress - 6, sizeof(ushort)))
                    : (ushort)0xffff;
                ushort sentSignal = BitConverter.ToUInt16(bytes, 0);
                if (sentSignal == 0xd164 && bytes.Length >= 6)
                {
                    pendingAdminReadId = BitConverter.ToUInt16(bytes, 2);
                }
                else if (sentSignal == 0xd165 &&
                    pendingAdminReadId is { } adminReadId &&
                    fakeAdminReadRange is { } range &&
                    adminReadId >= range.Start &&
                    adminReadId <= range.End &&
                    sentSignalAddress < ArmModemBus.InternalRamSize - 5)
                {
                    // Probe-only shortcut: mark the LLStore read successful without
                    // extending this OSE signal's allocation. The scan record payload
                    // is injected later into the caller's local output buffer at the
                    // 010ac74c copy point.
                    modem.Bus.WriteByte(sentSignalAddress + 2, 0, ArmAccess.None);
                    modem.Bus.WriteByte(sentSignalAddress + 3, 0, ArmAccess.None);
                    modem.Bus.WriteByte(sentSignalAddress + 4, 0, ArmAccess.None);
                    pendingFakeAdminReadId = adminReadId;
                    bytes = modem.Bus.SnapshotInternal(sentSignalAddress, 32);
                }
                oseSignalSends.Add(new(
                    modem.Cycles,
                    pc,
                    activeLinkManagerAction,
                    modem.Cpu.GetGpr(1),
                    sentSignal,
                    sentSignalSize,
                    modem.Cpu.GetGpr(14),
                    bytes));
            }
        }
    }

    if (pc == 0x010c1ad4 && enqueuedDspCommands.Count < 20_000)
    {
        uint address = modem.Cpu.GetGpr(0);
        if (address < ArmModemBus.InternalRamSize - 6)
        {
            byte[] header = modem.Bus.SnapshotInternal(address, 6);
            ushort outboundSignal = BitConverter.ToUInt16(header, 0);
            ushort length = BitConverter.ToUInt16(header, 4);
            if (outboundSignal == 0xc3ea && length <= 0x11 &&
                address + 6 + length <= ArmModemBus.InternalRamSize)
            {
                byte[] payload = modem.Bus.SnapshotInternal(address + 6, length);
                enqueuedDspCommands.Add(new(
                    modem.Cycles,
                    activeLinkManagerAction,
                    header[2],
                    payload));
            }
        }
    }

    if (pc != 0x010adb72 || linkManagerActions.Count >= 20_000)
    {
        return;
    }

    activeLinkManagerAction = (ushort)modem.Cpu.GetGpr(0);
    if (activeLinkManagerAction == tracedAction)
    {
        tracedActionSeen++;
        traceActiveLinkManagerAction = tracedActionSeen == tracedActionOccurrence;
    }
    linkManagerActionReturn = modem.Cpu.GetGpr(14) & ~1u;
    uint signalAddress = modem.Cpu.GetGpr(1);
    ushort signal = signalAddress < ArmModemBus.InternalRamSize - 1
        ? BitConverter.ToUInt16(modem.Bus.SnapshotInternal(signalAddress, 2))
        : (ushort)0xffff;
    if (activeLinkManagerAction == 0x0014 && signal == 0xc356 &&
        probeC356Byte10.HasValue &&
        signalAddress < ArmModemBus.InternalRamSize - 0x0b)
    {
        // Explicitly temporary field-domain probe after inspecting action 0014.
        modem.Bus.WriteByte(
            signalAddress + 10,
            probeC356Byte10.Value,
            ArmAccess.None);
    }
    byte[] signalBytes = signalAddress < ArmModemBus.InternalRamSize - 32
        ? modem.Bus.SnapshotInternal(signalAddress, 32)
        : [];
    uint state = BitConverter.ToUInt32(
        modem.Bus.SnapshotExternal(0x01400c3c, sizeof(uint)));
    activeLinkManagerSignal = signal;
    activeLinkManagerState = state;
    linkManagerActions.Add(new(
        modem.Cycles,
        (ushort)modem.Cpu.GetGpr(0),
        state,
        signal,
        signalAddress,
        signalBytes));
};
machine.Modem.Bus.Uart2ByteTransmitted += modemDebugBytes.Add;
machine.Modem!.Bus.MmioAccessed += access =>
{
    var key = new AccessKey(access.Pc, access.Address, access.Size, access.IsWrite);
    if (!accesses.TryGetValue(key, out var summary))
    {
        summary = new(access.Cycle);
        accesses.Add(key, summary);
    }
    summary.Observe(access.Cycle, access.Value);
    if (access.IsWrite && access.Address == 0x00800800 &&
        nextExperimentalDspFrameCycle == long.MaxValue)
    {
        nextExperimentalDspFrameCycle = access.Cycle + experimentalDspFrameCycles;
    }
    if (tracedDspStatus.HasValue && tracedDspStatusArmed &&
        !tracedDspStatusCycle.HasValue &&
        !access.IsWrite && access.Address == 0x00800800 && access.Size == 1 &&
        ((byte)access.Value & tracedDspStatus.Value) == tracedDspStatus.Value)
    {
        tracedDspStatusCycle = access.Cycle;
        tracedDspStatusInstructionsRemaining = 4_000;
    }
    if (access.IsWrite && access.Address is 0x00800804 or 0x0080080c)
    {
        dspPortWrites.Add(access);
    }
    if (access.IsWrite && access.Address == 0x00800804)
    {
        outboundDspPacket.Add((byte)access.Value);
        if (outboundDspPacket.Count >= 2 &&
            outboundDspPacket.Count == outboundDspPacket[1] + 2)
        {
            if (!tracedDspStatusArmed && tracedDspStatusAfterPacket is not null &&
                outboundDspPacket.SequenceEqual(tracedDspStatusAfterPacket))
            {
                tracedDspStatusArmed = true;
            }
            if (acknowledgeDspTransfers &&
                outboundDspPacket.Count == 3 &&
                outboundDspPacket[0] is >= 0x10 and <= 0x17)
            {
                scheduledDspStatuses.Enqueue(
                    0x08,
                    access.Cycle + 2 * experimentalDspFrameCycles);
            }
            if (acknowledgedDspPacketTypes.Contains(outboundDspPacket[0]))
            {
                scheduledDspStatuses.Enqueue(
                    0x08,
                    access.Cycle + 2 * experimentalDspFrameCycles);
            }
            bool rawTriggerMatched = oneShotRawTriggerPacket is null
                ? outboundDspPacket[0] == 0x45
                : outboundDspPacket.SequenceEqual(oneShotRawTriggerPacket);
            RawResponseRule? matchedRule = rawResponseRules.FirstOrDefault(
                rule => outboundDspPacket.SequenceEqual(rule.TriggerPacket));
            if (matchedRule is not null)
            {
                // Experimental protocol probes deliberately confined to this
                // diagnostic runner. Multi-rule mode lets one run answer
                // multiple firmware-owned DSP commands without production
                // shortcuts.
                if (!matchedRule.Sent || repeatRawResponse)
                {
                    foreach (byte[] response in matchedRule.Responses)
                    {
                        machine.Modem.Bus.QueueDspInboundTransfer(
                            oneShotRawControl,
                            oneShotRawKind,
                            response);
                        if (oneShotRawStatus.HasValue)
                        {
                            machine.Modem.Bus.AssertDspStatus(oneShotRawStatus.Value);
                        }
                    }
                    matchedRule.Sent = true;
                }
            }
            else if (rawTriggerMatched)
            {
                // Experimental protocol probes deliberately confined to this
                // diagnostic runner. The explicit raw response is delivered
                // once; the default candidate measurement follows each 0x45.
                if (oneShotRawSequence.Length != 0 &&
                    (!oneShotRawResponseSent || repeatRawResponse))
                {
                    foreach (byte[] response in oneShotRawSequence)
                    {
                        machine.Modem.Bus.QueueDspInboundTransfer(
                            oneShotRawControl,
                            oneShotRawKind,
                            response);
                        if (oneShotRawStatus.HasValue)
                        {
                            machine.Modem.Bus.AssertDspStatus(oneShotRawStatus.Value);
                        }
                    }
                    if (!repeatRawResponse)
                    {
                        oneShotRawResponseSent = true;
                    }
                }
                else if (oneShotRawResponse is not null &&
                    (!oneShotRawResponseSent || repeatRawResponse))
                {
                    machine.Modem.Bus.QueueDspInboundTransfer(
                        oneShotRawControl,
                        oneShotRawKind,
                        oneShotRawResponse);
                    if (oneShotRawStatus.HasValue)
                    {
                        machine.Modem.Bus.AssertDspStatus(oneShotRawStatus.Value);
                    }
                    if (!repeatRawResponse)
                    {
                        oneShotRawResponseSent = true;
                    }
                }
                else if (oneShotRawResponse is null && !suppressDefaultDspResponse)
                {
                    machine.Modem.Bus.QueueDspInboundPayload(
                        [0x0c, measurement, measurement]);
                }
            }
            else if (outboundDspPacket[0] == 0x45 &&
                oneShotRawTriggerPacket is not null &&
                oneShotRawResponse is null &&
                !suppressDefaultDspResponse)
            {
                machine.Modem.Bus.QueueDspInboundPayload(
                    [0x0c, measurement, measurement]);
            }
            outboundDspPacket.Clear();
        }
    }
    if (access.Address is >= 0x00800800 and <= 0x00800810 && dspPortAccesses.Count < 1_000)
    {
        dspPortAccesses.Add(access);
    }
};

while (machine.ExecutedInstructions < instructionLimit && !machine.IsStopped)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"AVR instructions={machine.ExecutedInstructions:n0} cycles={machine.Cycles:n0} " +
    $"ARM instructions={machine.Modem.Instructions:n0} cycles={machine.Modem.Cycles:n0} " +
    $"stopped={machine.IsStopped} {machine.StopReason} " +
    $"ARM-stopped={machine.Modem.IsStopped} {machine.Modem.StopReason}");
Console.WriteLine("Physical-output latches:");
foreach (string outputEvent in physicalOutputEvents)
{
    Console.WriteLine("  " + outputEvent);
}
Console.WriteLine("LLGPIO_SetPin calls:");
foreach (var call in llGpioSetPinCalls)
{
    Console.WriteLine(
        $"  cycle={call.Cycle:n0} pin=0x{call.Pin:x2} state=0x{call.State:x2} " +
        $"return=0x{call.Return:x8}");
}
Console.WriteLine("LLGPIO_Configure calls:");
foreach (var call in llGpioConfigureCalls)
{
    Console.WriteLine(
        $"  cycle={call.Cycle:n0} pin=0x{call.Pin:x2} direction=0x{call.Direction:x2} " +
        $"return=0x{call.Return:x8}");
}
Console.WriteLine(
    $"D2D4 allocation probe count={enlargedD2d5AllocationCount} " +
    $"header={Convert.ToHexStringLower(d2d4AllocationHeader)}");
Console.WriteLine(
    $"DSP report recipient @0x81f8=" +
    $"0x{BitConverter.ToUInt16(machine.Modem.Bus.SnapshotInternal(0x81f8, 2)):x4}");
Console.WriteLine(
    $"OSE PCB pid=4 @0x8498=" +
    Convert.ToHexStringLower(machine.Modem.Bus.SnapshotInternal(0x8498, 0x60)));
Console.WriteLine(
    "LM context @0x014214e0=" +
    Convert.ToHexStringLower(machine.Modem.Bus.SnapshotExternal(0x014214e0, 0x100)));
Console.WriteLine(
    "LM driver @0x01400c40=" +
    Convert.ToHexStringLower(machine.Modem.Bus.SnapshotExternal(0x01400c40, 0x100)));
Console.WriteLine(
    "Modem debug=" + new string(modemDebugBytes.Select(value =>
        value is >= 0x20 and <= 0x7e ? (char)value : '.').ToArray()));
foreach (var group in accesses
    .OrderBy(pair => pair.Key.Address)
    .ThenBy(pair => pair.Key.IsWrite)
    .ThenByDescending(pair => pair.Value.Count)
    .ThenBy(pair => pair.Key.Pc)
    .GroupBy(pair => pair.Key.Address))
{
    Console.WriteLine($"0x{group.Key:x8}");
    foreach (var pair in group)
    {
        AccessKey key = pair.Key;
        AccessSummary summary = pair.Value;
        string values = summary.ValuesOverflowed
            ? string.Join(',', summary.Values.Select(value => $"{value:x}")) + ",..."
            : string.Join(',', summary.Values.Select(value => $"{value:x}"));
        Console.WriteLine(
            $"  {(key.IsWrite ? 'W' : 'R')}{key.Size} pc=0x{key.Pc:x8} " +
            $"count={summary.Count:n0} first={summary.FirstCycle:n0} " +
            $"last={summary.LastCycle:n0} values={values}");
    }
}

Console.WriteLine("DSP command packets (0x00800804):");
var commandBytes = dspPortWrites
    .Where(access => access.Address == 0x00800804)
    .ToArray();
for (var index = 0; index < commandBytes.Length;)
{
    if (index + 2 > commandBytes.Length)
    {
        Console.WriteLine($"  truncated header at byte {index}");
        break;
    }
    int length = (byte)commandBytes[index + 1].Value;
    int packetLength = 2 + length;
    if (index + packetLength > commandBytes.Length)
    {
        Console.WriteLine(
            $"  truncated packet at byte {index}: type={(byte)commandBytes[index].Value:x2} " +
            $"length={length} available={commandBytes.Length - index - 2}");
        break;
    }
    string packet = Convert.ToHexStringLower(commandBytes
        .AsSpan(index, packetLength)
        .ToArray()
        .Select(access => (byte)access.Value)
        .ToArray());
    Console.WriteLine(
        $"  cycle={commandBytes[index].Cycle:n0} pc=0x{commandBytes[index].Pc:x8} {packet}");
    index += packetLength;
}

Console.WriteLine("Link-manager actions:");
foreach (var action in linkManagerActions)
{
    Console.WriteLine(
        $"  cycle={action.Cycle:n0} action=0x{action.Action:x4} " +
        $"state=0x{action.State:x4} " +
        $"signal=0x{action.Signal:x4} address=0x{action.SignalAddress:x8} " +
        $"bytes={Convert.ToHexStringLower(action.SignalBytes)}");
}

Console.WriteLine("Link-manager action returns:");
foreach (var actionReturn in linkManagerActionReturns)
{
    Console.WriteLine(
        $"  cycle={actionReturn.Cycle:n0} action=0x{actionReturn.Action:x4} " +
        $"entry-state=0x{actionReturn.EntryState:x4} " +
        $"signal=0x{actionReturn.Signal:x4} return=0x{actionReturn.ReturnValue:x8}");
}

Console.WriteLine("Link-manager state transition map:");
foreach (var mapping in linkManagerStateMappings)
{
    Console.WriteLine(
        $"  state=0x{mapping.State:x4} index={mapping.Index} " +
        $"signal=0x{mapping.Signal:x4} action=0x{mapping.Action:x4}");
}

Console.WriteLine("Link-manager state changes:");
foreach (var change in linkManagerStateChanges)
{
    Console.WriteLine(
        $"  cycle={change.Cycle:n0} pc=0x{change.PreviousPc:x8}->0x{change.Pc:x8} " +
        $"state=0x{change.PreviousState:x4}->0x{change.State:x4} " +
        $"action={(change.Action.HasValue ? $"0x{change.Action:x4}" : "none")} " +
        $"signal=0x{change.Signal:x4} r0=0x{change.R0:x8} r1=0x{change.R1:x8} " +
        $"r2=0x{change.R2:x8} r3=0x{change.R3:x8} r4=0x{change.R4:x8} " +
        $"lr=0x{change.Lr:x8}");
}

Console.WriteLine("Control signal producer events:");
foreach (var producerEvent in controlSignalProducerEvents)
{
    Console.WriteLine(
        $"  cycle={producerEvent.Cycle:n0} pc=0x{producerEvent.Pc:x8} " +
        $"signal=0x{producerEvent.Signal:x4} r0=0x{producerEvent.R0:x8} " +
        $"r1=0x{producerEvent.R1:x8} r2=0x{producerEvent.R2:x8} " +
        $"r3=0x{producerEvent.R3:x8} lr=0x{producerEvent.Lr:x8}");
}

Console.WriteLine("Decoded DSP records:");
foreach (var record in decodedDspRecords)
{
    Console.WriteLine(
        $"  cycle={record.Cycle:n0} record={Convert.ToHexStringLower(record.Record)} " +
        $"signal=0x{record.Signal:x4} address=0x{record.SignalAddress:x8} " +
        $"bytes={Convert.ToHexStringLower(record.SignalBytes)}");
}

Console.WriteLine("Fake admin read payload injections:");
foreach (var injection in fakeAdminReadPayloadInjections)
{
    Console.WriteLine(
        $"  cycle={injection.Cycle:n0} pc=0x{injection.Pc:x8} " +
        $"id=0x{injection.Id:x4} sp=0x{injection.StackPointer:x8} " +
        $"payload={Convert.ToHexStringLower(injection.Payload)}");
}

Console.WriteLine("Admin-read helper events:");
foreach (var helperEvent in adminReadHelperEvents)
{
    Console.WriteLine(
        $"  cycle={helperEvent.Cycle:n0} {(helperEvent.IsReturn ? "return" : "entry")} " +
        $"id=0x{helperEvent.Id:x4} destination=0x{helperEvent.Destination:x8} " +
        $"lr=0x{helperEvent.Lr:x8} " +
        $"result={Convert.ToHexStringLower(helperEvent.Result)}");
}

Console.WriteLine("OSE signal receives during link-manager actions:");
foreach (var receive in oseSignalReceiveEvents.Where(receive =>
    receive.Action.HasValue || receive.Signal == tracedReceiveSignal ||
    receive.Signal is 0xd165 or 0xc3e5 or 0xc3e6 or 0xc3f2))
{
    Console.WriteLine(
        $"  cycle={receive.Cycle:n0} pc=0x{receive.Pc:x8} " +
        $"action={(receive.Action.HasValue ? $"0x{receive.Action:x4}" : "none")} " +
        $"signal=0x{receive.Signal:x4} address=0x{receive.SignalAddress:x8} " +
        $"lr=0x{receive.Lr:x8} bytes={Convert.ToHexStringLower(receive.Bytes)}");
}

Console.WriteLine("Enqueued DSP commands:");
foreach (var command in enqueuedDspCommands)
{
    Console.WriteLine(
        $"  cycle={command.Cycle:n0} " +
        $"action={(command.Action.HasValue ? $"0x{command.Action:x4}" : "none")} " +
        $"packet={command.Type:x2}{command.Payload.Length:x2}" +
        Convert.ToHexStringLower(command.Payload));
}

Console.WriteLine("OSE signal sends during link-manager actions:");
foreach (var send in oseSignalSends.Where(send =>
    send.Action.HasValue || send.Signal is 0xd2d5 or 0xc3dd or 0xc3e6 or 0xc3f2 or 0xc3f7))
{
    Console.WriteLine(
        $"  cycle={send.Cycle:n0} pc=0x{send.Pc:x8} " +
        $"action=0x{send.Action:x4} " +
        $"pid=0x{send.Pid:x4} signal=0x{send.Signal:x4} size=0x{send.Size:x4} " +
        $"lr=0x{send.Lr:x8} bytes={Convert.ToHexStringLower(send.Bytes)}");
}

Console.WriteLine("Driver C3EF receive attempts:");
foreach (var receive in driverVariableEvents)
{
    Console.WriteLine(
        $"  cycle={receive.Cycle:n0} address=0x{receive.SignalAddress:x8} " +
        $"state=0x{receive.State:x2} bytes={Convert.ToHexStringLower(receive.Signal)}");
}

Console.WriteLine("Driver variable-transfer path:");
foreach (var pathEvent in driverPathEvents)
{
    Console.WriteLine(
        $"  cycle={pathEvent.Cycle:n0} pc=0x{pathEvent.Pc:x8} " +
        $"context={Convert.ToHexStringLower(pathEvent.Context)}");
}

Console.WriteLine("DSP FIFO copy events:");
foreach (var copyEvent in dspFifoCopyEvents)
{
    Console.WriteLine(
        $"  cycle={copyEvent.Cycle:n0} pc=0x{copyEvent.Pc:x8} " +
        $"r0=0x{copyEvent.R0:x8} r1=0x{copyEvent.R1:x8} " +
        $"r2=0x{copyEvent.R2:x8} r3=0x{copyEvent.R3:x8} " +
        $"lr=0x{copyEvent.Lr:x8} " +
        $"dest={Convert.ToHexStringLower(copyEvent.Destination)}");
}

Console.WriteLine("DSP variable completion events:");
foreach (var completeEvent in dspVariableCompleteEvents)
{
    Console.WriteLine(
        $"  cycle={completeEvent.Cycle:n0} pc=0x{completeEvent.Pc:x8} " +
        $"r0=0x{completeEvent.R0:x8} r1=0x{completeEvent.R1:x8} " +
        $"r2=0x{completeEvent.R2:x8} lr=0x{completeEvent.Lr:x8} " +
        $"signal=0x{completeEvent.SignalAddress:x8} " +
        $"bytes={Convert.ToHexStringLower(completeEvent.Signal)} " +
        $"context={Convert.ToHexStringLower(completeEvent.Context)}");
}

Console.WriteLine("DSP variable free events:");
foreach (var freeEvent in dspVariableFreeEvents)
{
    Console.WriteLine(
        $"  cycle={freeEvent.Cycle:n0} pc=0x{freeEvent.Pc:x8} " +
        $"slot=0x{freeEvent.SlotAddress:x8} signal=0x{freeEvent.SignalAddress:x8} " +
        $"lr=0x{freeEvent.Lr:x8} " +
        $"bytes={Convert.ToHexStringLower(freeEvent.Signal)} " +
        $"ring={Convert.ToHexStringLower(freeEvent.Ring)}");
}

Console.WriteLine("OSE signal free events:");
foreach (var freeEvent in oseSignalFreeEvents)
{
    Console.WriteLine(
        $"  cycle={freeEvent.Cycle:n0} pc=0x{freeEvent.Pc:x8} " +
        $"slot=0x{freeEvent.SlotAddress:x8} signal=0x{freeEvent.SignalAddress:x8} " +
        $"lr=0x{freeEvent.Lr:x8} descriptor=0x{freeEvent.Descriptor:x8} " +
        $"table=0x{freeEvent.SignalTable:x8} router-state=0x{freeEvent.RouterState:x4} " +
        $"bytes={Convert.ToHexStringLower(freeEvent.Signal)}");
}

if (tracedAction.HasValue)
{
    Console.WriteLine($"First action 0x{tracedAction:x4} instruction trace:");
    foreach (var run in CompressPcRuns(tracedActionInstructions))
    {
        Console.WriteLine(
            $"  0x{run.Start:x8}..0x{run.End:x8} step={run.Step} count={run.Count}");
    }
    Console.WriteLine($"First action 0x{tracedAction:x4} register changes:");
    ActionRegisterSnapshot? previous = null;
    foreach (var snapshot in tracedActionRegisters)
    {
        if (previous is null || !snapshot.RegistersEqual(previous.Value))
        {
            Console.WriteLine(
                $"  pc=0x{snapshot.Pc:x8} r0={snapshot.R0:x8} r1={snapshot.R1:x8} " +
                $"r2={snapshot.R2:x8} r3={snapshot.R3:x8} r4={snapshot.R4:x8} " +
                $"r7={snapshot.R7:x8}");
        }
        previous = snapshot;
    }
}

if (tracedDspStatus.HasValue)
{
    Console.WriteLine(
        $"First DSP status 0x{tracedDspStatus:x2} instruction trace " +
        $"after cycle={tracedDspStatusCycle?.ToString("n0") ?? "not observed"}:");
    foreach (var run in CompressPcRuns(tracedDspStatusInstructions))
    {
        Console.WriteLine(
            $"  0x{run.Start:x8}..0x{run.End:x8} step={run.Step} count={run.Count}");
    }
    Console.WriteLine($"First DSP status 0x{tracedDspStatus:x2} register changes:");
    DspStatusRegisterSnapshot? previous = null;
    foreach (var snapshot in tracedDspStatusRegisters)
    {
        if (previous is null || !snapshot.RegistersEqual(previous.Value))
        {
            Console.WriteLine(
                $"  pc=0x{snapshot.Pc:x8} " +
                $"r0={snapshot.R0:x8} r1={snapshot.R1:x8} " +
                $"r2={snapshot.R2:x8} r3={snapshot.R3:x8} " +
                $"r4={snapshot.R4:x8} r5={snapshot.R5:x8} " +
                $"r6={snapshot.R6:x8} r7={snapshot.R7:x8} " +
                $"r8={snapshot.R8:x8} r9={snapshot.R9:x8} " +
                $"r10={snapshot.R10:x8} r11={snapshot.R11:x8} " +
                $"r12={snapshot.R12:x8} sp={snapshot.Sp:x8} lr={snapshot.Lr:x8}");
        }
        previous = snapshot;
    }
}

if (tracedReceiveSignal.HasValue)
{
    Console.WriteLine($"First receive signal 0x{tracedReceiveSignal:x4} instruction trace:");
    foreach (var run in CompressPcRuns(tracedReceiveInstructions))
    {
        Console.WriteLine(
            $"  0x{run.Start:x8}..0x{run.End:x8} step={run.Step} count={run.Count}");
    }
    Console.WriteLine($"First receive signal 0x{tracedReceiveSignal:x4} register changes:");
    DspStatusRegisterSnapshot? previous = null;
    foreach (var snapshot in tracedReceiveRegisters)
    {
        if (previous is null || !snapshot.RegistersEqual(previous.Value))
        {
            Console.WriteLine(
                $"  pc=0x{snapshot.Pc:x8} " +
                $"r0={snapshot.R0:x8} r1={snapshot.R1:x8} " +
                $"r2={snapshot.R2:x8} r3={snapshot.R3:x8} " +
                $"r4={snapshot.R4:x8} r5={snapshot.R5:x8} " +
                $"r6={snapshot.R6:x8} r7={snapshot.R7:x8} " +
                $"r8={snapshot.R8:x8} r9={snapshot.R9:x8} " +
                $"r10={snapshot.R10:x8} r11={snapshot.R11:x8} " +
                $"r12={snapshot.R12:x8} sp={snapshot.Sp:x8} lr={snapshot.Lr:x8}");
        }
        previous = snapshot;
    }
}

Console.WriteLine("First DSP-port accesses:");
foreach (var access in dspPortAccesses)
{
    Console.WriteLine(
        $"  cycle={access.Cycle:n0} {(access.IsWrite ? 'W' : 'R')}{access.Size} " +
        $"pc=0x{access.Pc:x8} address=0x{access.Address:x8} value=0x{access.Value:x}");
}

Console.WriteLine("DSP secondary writes (0x0080080c):");
foreach (var access in dspPortWrites.Where(access => access.Address == 0x0080080c))
{
    Console.WriteLine(
        $"  cycle={access.Cycle:n0} pc=0x{access.Pc:x8} value={(byte)access.Value:x2}");
}

static byte[] SnapshotArmMemory(
    ArmModem modem,
    byte[] modemPayload,
    uint address,
    int length)
{
    if (address <= ArmModemBus.InternalRamSize - length)
    {
        return modem.Bus.SnapshotInternal(address, length);
    }
    if (address is >= ArmModemBus.ExternalRamBase &&
        address <= ArmModemBus.ExternalRamBase +
            ArmModemBus.ExternalRamMirrorSpan - length)
    {
        return modem.Bus.SnapshotExternal(address, length);
    }
    if (address >= modem.Image.LoadAddress &&
        address - modem.Image.LoadAddress <= modemPayload.Length - length)
    {
        return modemPayload.AsSpan(
            (int)(address - modem.Image.LoadAddress),
            length).ToArray();
    }
    return [];
}

static IEnumerable<PcRun> CompressPcRuns(IReadOnlyList<uint> pcs)
{
    for (var index = 0; index < pcs.Count;)
    {
        uint start = pcs[index];
        int step = index + 1 < pcs.Count
            ? unchecked((int)(pcs[index + 1] - pcs[index]))
            : 0;
        var endIndex = step == 0 ? index : index + 1;
        while (endIndex + 1 < pcs.Count &&
            unchecked((int)(pcs[endIndex + 1] - pcs[endIndex])) == step)
        {
            endIndex++;
        }
        yield return new(start, pcs[endIndex], step, endIndex - index + 1);
        index = endIndex + 1;
    }
}

static List<RawResponseRule> ParseRawResponseRules(string text)
{
    var rules = new List<RawResponseRule>();
    foreach (string ruleText in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] parts = ruleText.Split('=', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException(
                "MIA_DSP_RAW_RULES entries must be TRIGGER=PAYLOAD[,PAYLOAD...]");
        }
        byte[] trigger = Convert.FromHexString(parts[0]);
        byte[][] responses = parts[1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Convert.FromHexString)
            .ToArray();
        if (responses.Length == 0)
        {
            throw new ArgumentException(
                "MIA_DSP_RAW_RULES entries must include at least one payload");
        }
        rules.Add(new(trigger, responses));
    }
    return rules;
}

static (ushort Start, ushort End) ParseRange(string text)
{
    string[] parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
    if (parts.Length != 2)
    {
        throw new ArgumentException("range must be START-END");
    }
    ushort start = Convert.ToUInt16(parts[0], 16);
    ushort end = Convert.ToUInt16(parts[1], 16);
    if (end < start)
    {
        throw new ArgumentException("range end must be >= start");
    }
    return (start, end);
}

return 0;

sealed class RawResponseRule(byte[] triggerPacket, byte[][] responses)
{
    public byte[] TriggerPacket { get; } = triggerPacket;

    public byte[][] Responses { get; } = responses;

    public bool Sent { get; set; }
}

readonly record struct AccessKey(uint Pc, uint Address, int Size, bool IsWrite);

readonly record struct LinkManagerAction(
    long Cycle,
    ushort Action,
    uint State,
    ushort Signal,
    uint SignalAddress,
    byte[] SignalBytes);

readonly record struct LinkManagerActionReturn(
    long Cycle,
    ushort Action,
    uint EntryState,
    ushort Signal,
    uint ReturnValue);

readonly record struct LinkManagerStateMapping(
    int State,
    int Index,
    ushort Signal,
    ushort Action);

readonly record struct ControlSignalProducerEvent(
    long Cycle,
    uint Pc,
    ushort Signal,
    uint R0,
    uint R1,
    uint R2,
    uint R3,
    uint Lr);

readonly record struct LinkManagerStateChange(
    long Cycle,
    uint PreviousPc,
    uint Pc,
    uint PreviousState,
    uint State,
    ushort? Action,
    ushort Signal,
    uint R0,
    uint R1,
    uint R2,
    uint R3,
    uint R4,
    uint Lr);

readonly record struct DecodedDspRecord(
    long Cycle,
    byte[] Record,
    uint SignalAddress,
    ushort Signal,
    byte[] SignalBytes);

readonly record struct FakeAdminReadPayloadInjection(
    long Cycle,
    uint Pc,
    ushort Id,
    uint StackPointer,
    byte[] Payload);

readonly record struct AdminReadHelperEvent(
    long Cycle,
    ushort Id,
    uint Destination,
    uint Lr,
    bool IsReturn,
    byte[] Result);

readonly record struct EnqueuedDspCommand(
    long Cycle,
    ushort? Action,
    byte Type,
    byte[] Payload);

readonly record struct OseSignalSend(
    long Cycle,
    uint Pc,
    ushort? Action,
    uint Pid,
    ushort Signal,
    ushort Size,
    uint Lr,
    byte[] Bytes);

readonly record struct OseSignalReceiveEvent(
    long Cycle,
    uint Pc,
    ushort? Action,
    uint SignalAddress,
    ushort Signal,
    uint Lr,
    byte[] Bytes);

readonly record struct DriverVariableEvent(
    long Cycle,
    uint SignalAddress,
    byte State,
    byte[] Signal);

readonly record struct DriverPathEvent(long Cycle, uint Pc, byte[] Context);

readonly record struct DspFifoCopyEvent(
    long Cycle,
    uint Pc,
    uint R0,
    uint R1,
    uint R2,
    uint R3,
    uint Lr,
    byte[] Destination);

readonly record struct DspVariableCompleteEvent(
    long Cycle,
    uint Pc,
    uint R0,
    uint R1,
    uint R2,
    uint Lr,
    uint SignalAddress,
    byte[] Signal,
    byte[] Context);

readonly record struct DspVariableFreeEvent(
    long Cycle,
    uint Pc,
    uint SlotAddress,
    uint SignalAddress,
    uint Lr,
    byte[] Signal,
    byte[] Ring);

readonly record struct OseSignalFreeEvent(
    long Cycle,
    uint Pc,
    uint SlotAddress,
    uint SignalAddress,
    uint Lr,
    uint Descriptor,
    uint SignalTable,
    uint RouterState,
    byte[] Signal);

readonly record struct PcRun(uint Start, uint End, int Step, int Count);

readonly record struct ActionRegisterSnapshot(
    uint Pc,
    uint R0,
    uint R1,
    uint R2,
    uint R3,
    uint R4,
    uint R7)
{
    public bool RegistersEqual(ActionRegisterSnapshot other) =>
        R0 == other.R0 && R1 == other.R1 && R2 == other.R2 &&
        R3 == other.R3 && R4 == other.R4 && R7 == other.R7;
}

readonly record struct DspStatusRegisterSnapshot(
    uint Pc,
    uint R0,
    uint R1,
    uint R2,
    uint R3,
    uint R4,
    uint R5,
    uint R6,
    uint R7,
    uint R8,
    uint R9,
    uint R10,
    uint R11,
    uint R12,
    uint Sp,
    uint Lr)
{
    public bool RegistersEqual(DspStatusRegisterSnapshot other) =>
        R0 == other.R0 && R1 == other.R1 && R2 == other.R2 &&
        R3 == other.R3 && R4 == other.R4 && R5 == other.R5 &&
        R6 == other.R6 && R7 == other.R7 && R8 == other.R8 &&
        R9 == other.R9 && R10 == other.R10 && R11 == other.R11 &&
        R12 == other.R12 && Sp == other.Sp && Lr == other.Lr;
}

sealed class AccessSummary(long firstCycle)
{
    const int MaximumValues = 16;

    public long Count { get; private set; }

    public long FirstCycle { get; } = firstCycle;

    public long LastCycle { get; private set; } = firstCycle;

    public SortedSet<uint> Values { get; } = [];

    public bool ValuesOverflowed { get; private set; }

    public void Observe(long cycle, uint value)
    {
        Count++;
        LastCycle = cycle;
        if (!ValuesOverflowed && Values.Count < MaximumValues)
        {
            Values.Add(value);
        }
        else if (!Values.Contains(value))
        {
            ValuesOverflowed = true;
        }
    }
}
