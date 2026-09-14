#!/usr/bin/env dotnet
#:property AssemblyName=AvrCore.Tests
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AvrCore.Execution;
using Mia.Emulator.Asic;
using Mia.Emulator.Diagnostics;
using Mia.Emulator.Display;
using Mia.Emulator.Input;
using Mia.Emulator.Machine;
using Mia.Emulator.Runtime;
using ProbeScenarioResult = (
    System.Collections.Generic.Dictionary<string, long> Numbers,
    System.Collections.Generic.Dictionary<string, string> Text,
    System.Collections.Generic.IReadOnlyList<
        Mia.Emulator.Diagnostics.AccessTraceReconciliationSnapshot>?
        Reconciliation);
using AccessCaptureRun = (
    int Order,
    string PairId,
    string Scenario,
    System.Collections.Generic.Dictionary<string, long> Numbers,
    System.Collections.Generic.Dictionary<string, string> Text,
    System.Collections.Generic.IReadOnlyList<
        Mia.Emulator.Diagnostics.AccessTraceReconciliationSnapshot>
        Reconciliation);
using LedServiceSignalEvent = (
    long Cycle,
    long Instruction,
    byte Process,
    byte PeerProcess,
    int ReturnPc,
    int LogicalSignalAddress,
    int PhysicalSignalAddress,
    ushort Signal,
    string SignalBytes,
    int LogicalBufferAddress,
    int PhysicalBufferAddress,
    string BufferBytes);
using LedServiceI2cTransaction = (
    long Cycle,
    long Instruction,
    int Pc,
    byte Process,
    byte Address,
    string Payload);
using LedServiceAvrWrite = (
    long AsicCycle,
    long AvrCycle,
    int Pc,
    int StackPointer,
    byte Process,
    int Address,
    byte OldValue,
    byte NewValue,
    byte Mask,
    byte EffectiveValue,
    bool HookConsumed,
    string Registers,
    string StackBytes);
using LedServiceExecutionEvent = (
    long Cycle,
    int Pc,
    byte Process,
    string StateBytes,
    string PortBytes,
    string Registers,
    string StackBytes);

const string ExactServiceSequence = ">*<<*<*";
const string StandbyHash =
    "79b62efe3a38514b728c96d9b30b1929c30921a34e9e41ec1849019c8c12614c";
const string ServiceMenuHash =
    "57e53f3075ba498eff54ce5a160abd15e2048c36cf73da3daf1b467dfda84d9e";
const string ServiceTestsHash =
    "9ae366435e49127a62a18323b6b55aaab2317a649d5a7c727d7a793bb0a30787";
const string LedSelectedHash =
    "c6a76e2a052245fb47cdcb8fcbb567405768f658b170c68de2af906d97f3c2ba";
const string LedTestHash =
    "565422fb2feb4009924108c17063a101b783998d8162be5d47be7ca3883e1970";
const string FirmwarePath = "flat.bin";
const string GdfsPath = "images/T68i_Full_GDFS.compact.raw";
const string ModemPath = "images/t68i_R8A015_125326_Modem.bih";
const string SimIdentity = "321A065432100654";
const string ArtifactDirectory = "/tmp/mia-led-service-menu-ui";
const string AccessCaptureManifestName = "manifest.json";
const string AccessCaptureManifestSchema =
    "mia-led-service-access-pairs-v1";
const int AccessCapturePairCount = 3;
const long BootInstructionBudget = 200_000_000;
const long IdleStabilityCycleBudget = 6_500_000;
const long KeyDownCycleBudget = 1_500_000;
const long TriggerKeyDownCycleBudget = 3_000_000;
const long KeyPostCycleBudget = 6_500_000;
const long MenuSettleCycleBudget = 19_500_000;
const long ObservationStartCycle = 300_000_000;
const long ObservationWindowCycleBudget = 379_166_667;
const long ObservationInstructionSafetyBudget = 500_000_000;
const long DisassemblyTraceCycleBudget = 52_000_000;

try
{
    bool verificationMode = args.Length == 1 && args[0] is
        "--verify-idle" or
        "--verify-menu" or
        "--verify-ui" or
        "--verify-control" or
        "--verify-boundaries";
    bool captureMode = args.Length == 2 &&
        args[0] == "--capture-access-pairs" &&
        !string.IsNullOrWhiteSpace(args[1]);
    bool disassemblyTraceMode = args.Length == 2 &&
        args[0] == "--trace-disassembly" &&
        !string.IsNullOrWhiteSpace(args[1]);
    if (!verificationMode && !captureMode && !disassemblyTraceMode)
    {
        Console.Error.WriteLine(
            "Usage: dotnet run tools/probe_led_service_menu.cs -- " +
            "--verify-idle|--verify-menu|--verify-ui|--verify-control|" +
            "--verify-boundaries|--capture-access-pairs OUTPUT_DIR|" +
            "--trace-disassembly OUTPUT_JSON");
        return 64;
    }

    byte[] firmware = File.ReadAllBytes(FirmwarePath);
    byte[] gdfs = File.ReadAllBytes(GdfsPath);
    byte[] modem = File.ReadAllBytes(ModemPath);
    byte[] sim = Convert.FromHexString(SimIdentity);

    if (captureMode)
    {
        CaptureAccessPairs(args[1]);
        return 0;
    }

    if (disassemblyTraceMode)
    {
        var trace = RunScenario(
            "disassembly-trace",
            finalStage: 3,
            triggerTest: true,
            writeArtifacts: false,
            disassemblyTracePath: args[1]);
        Console.WriteLine(
            $"SERVICE_LED_DISASSEMBLY_TRACE PASS path={args[1]} " +
            $"start={trace.Numbers["disassembly_trace_start_cycle"]} " +
            $"end={trace.Numbers["disassembly_trace_end_cycle"]} " +
            $"sends={trace.Numbers["disassembly_trace_send_count"]} " +
            $"i2c={trace.Numbers["disassembly_trace_i2c_count"]}");
        return 0;
    }

    if (args[0] == "--verify-idle")
    {
        var idle = RunScenario(
            "idle-check",
            finalStage: 1,
            triggerTest: false,
            writeArtifacts: false);
        Console.WriteLine(
            $"SERVICE_IDLE_VERIFY PASS cycle={idle.Numbers["idle_stable_cycle"]} " +
            $"hash={idle.Text["idle_hash"]}");
        return 0;
    }

    if (args[0] == "--verify-menu")
    {
        var menu = RunScenario(
            "menu-check",
            finalStage: 2,
            triggerTest: false,
            writeArtifacts: false);
        Console.WriteLine(
            $"SERVICE_MENU_VERIFY PASS sequence={ExactServiceSequence} " +
            $"cycle={menu.Numbers["menu_cycle"]} hash={menu.Text["menu_hash"]}");
        return 0;
    }

    var service = RunScenario(
        "service",
        finalStage: 3,
        triggerTest: true,
        writeArtifacts: true);
    if (args[0] == "--verify-ui")
    {
        WriteArtifactSummary(service, control: null);
        Console.WriteLine(
            $"SERVICE_LED_UI_VERIFY PASS selected_cycle=" +
            $"{service.Numbers["selected_cycle"]} test_hash=" +
            service.Text["window_end_hash"]);
        return 0;
    }

    var control = RunScenario(
        "control",
        finalStage: 3,
        triggerTest: false,
        writeArtifacts: true);
    VerifyMatchedRuns(service, control);
    WriteArtifactSummary(service, control);

    if (args[0] == "--verify-control")
    {
        Console.WriteLine(
            $"SERVICE_CONTROL_VERIFY PASS selected_hash={control.Text["selected_hash"]} " +
            $"start_cycle={control.Numbers["window_start_cycle"]} " +
            $"window_cycles={ObservationWindowCycleBudget} center_triggers=" +
            $"{control.Numbers["center_trigger_count"]} in_window_keys=" +
            control.Numbers["window_key_event_count"]);
        return 0;
    }

    VerifyExactBoundaries(service, control);
    Console.WriteLine(
        $"SERVICE_TRACE_BOUNDARY_VERIFY PASS attach=" +
        $"{service.Numbers["attach_cycle"]} trigger=" +
        $"{service.Numbers["trigger_cycle"]} detach=" +
        $"{service.Numbers["detach_cycle"]} exit=" +
        $"{service.Numbers["exit_cycle"]} window_cycles=" +
        ObservationWindowCycleBudget);
    return 0;

    void CaptureAccessPairs(string outputDirectory)
    {
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Require(
            !File.Exists(fullOutputDirectory),
            $"Capture output path is a file: {fullOutputDirectory}.");
        Directory.CreateDirectory(fullOutputDirectory);

        var expectedPaths = new List<string>(AccessCapturePairCount * 2 + 1)
        {
            Path.Combine(fullOutputDirectory, AccessCaptureManifestName),
        };
        for (var pairIndex = 1;
             pairIndex <= AccessCapturePairCount;
             pairIndex++)
        {
            string pairId = $"pair-{pairIndex:00}";
            expectedPaths.Add(Path.Combine(
                fullOutputDirectory,
                $"{pairId}-service.json.gz"));
            expectedPaths.Add(Path.Combine(
                fullOutputDirectory,
                $"{pairId}-control.json.gz"));
        }
        foreach (string expectedPath in expectedPaths)
        {
            Require(
                !File.Exists(expectedPath) && !Directory.Exists(expectedPath),
                $"Refusing to overwrite capture output {expectedPath}.");
        }

        var captures = new List<AccessCaptureRun>(AccessCapturePairCount * 2);
        var runOrder = 1;
        for (var pairIndex = 1;
             pairIndex <= AccessCapturePairCount;
             pairIndex++)
        {
            string pairId = $"pair-{pairIndex:00}";
            var service = RunScenario(
                $"{pairId}-service",
                finalStage: 3,
                triggerTest: true,
                writeArtifacts: false,
                accessTracePath: Path.Combine(
                    fullOutputDirectory,
                    $"{pairId}-service.json.gz"));
            captures.Add((
                runOrder++,
                pairId,
                "service",
                service.Numbers,
                service.Text,
                service.Reconciliation ?? throw new InvalidOperationException(
                    $"{pairId}: service access reconciliation is missing.")));

            var control = RunScenario(
                $"{pairId}-control",
                finalStage: 3,
                triggerTest: false,
                writeArtifacts: false,
                accessTracePath: Path.Combine(
                    fullOutputDirectory,
                    $"{pairId}-control.json.gz"));
            captures.Add((
                runOrder++,
                pairId,
                "control",
                control.Numbers,
                control.Text,
                control.Reconciliation ?? throw new InvalidOperationException(
                    $"{pairId}: control access reconciliation is missing.")));

            VerifyMatchedRuns(service, control);
            VerifyAccessCapturePair(pairId, service, control);
        }

        Require(
            captures.Count == AccessCapturePairCount * 2,
            "Access capture did not produce exactly three matched pairs.");
        string manifestPath = Path.Combine(
            fullOutputDirectory,
            AccessCaptureManifestName);
        WriteAccessCaptureManifest(manifestPath, captures);
        Console.WriteLine(
            $"SERVICE_ACCESS_PAIR_CAPTURE PASS pairs={AccessCapturePairCount} " +
            $"runs={captures.Count} manifest={manifestPath}");
    }

    ProbeScenarioResult
        RunScenario(
            string scenario,
            int finalStage,
            bool triggerTest,
            bool writeArtifacts,
            string? accessTracePath = null,
            string? disassemblyTracePath = null)
    {
        Require(
            accessTracePath is null ||
                (finalStage == 3 &&
                 accessTracePath.EndsWith(".json.gz", StringComparison.Ordinal)),
            $"{scenario}: access traces require a full scenario and a .json.gz path.");
        Require(
            disassemblyTracePath is null ||
                (finalStage == 3 && triggerTest && accessTracePath is null &&
                 disassemblyTracePath.EndsWith(".json", StringComparison.Ordinal)),
            $"{scenario}: disassembly traces require a triggered full scenario, " +
            "no aggregate trace, and a .json path.");
        var numbers = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["boot_instruction_budget"] = BootInstructionBudget,
            ["idle_cycle"] = -1,
            ["idle_instruction"] = -1,
            ["idle_stable_cycle"] = -1,
            ["idle_stable_instruction"] = -1,
            ["menu_cycle"] = -1,
            ["menu_instruction"] = -1,
            ["selected_cycle"] = -1,
            ["selected_instruction"] = -1,
            ["window_start_cycle"] = -1,
            ["window_end_cycle"] = -1,
            ["attach_cycle"] = -1,
            ["trigger_cycle"] = -1,
            ["detach_cycle"] = -1,
            ["exit_cycle"] = -1,
            ["attach_ordinal"] = -1,
            ["trigger_ordinal"] = -1,
            ["detach_ordinal"] = -1,
            ["exit_ordinal"] = -1,
            ["center_trigger_count"] = 0,
            ["window_key_event_count"] = 0,
            ["observation_callback_count"] = 0,
            ["observation_first_cycle"] = -1,
            ["observation_last_cycle"] = -1,
            ["led_test_callback_count"] = 0,
            ["led_selected_callback_count"] = 0,
            ["callback_count_after_exit"] = 0,
            ["window_start_instruction"] = -1,
            ["window_end_instruction"] = -1,
            ["access_trace_attach_cycle"] = -1,
            ["access_trace_attach_ordinal"] = -1,
            ["access_trace_stop_call_cycle"] = -1,
            ["access_trace_stop_ordinal"] = -1,
            ["control_hold_begin_cycle"] = -1,
            ["control_hold_begin_ordinal"] = -1,
            ["control_hold_end_cycle"] = -1,
            ["control_hold_end_ordinal"] = -1,
            ["access_trace_start_cycle"] = -1,
            ["access_trace_end_cycle_exclusive"] = -1,
            ["access_trace_duration_cycles"] = -1,
            ["access_trace_bin_count"] = -1,
            ["access_trace_stopped_cycle"] = -1,
            ["access_trace_compressed_bytes"] = -1,
            ["access_trace_address_count"] = -1,
            ["access_trace_time_bin_cell_count"] = -1,
            ["access_trace_bin_value_cell_count"] = -1,
            ["access_trace_value_histogram_cell_count"] = -1,
            ["access_trace_transition_cell_count"] = -1,
            ["access_trace_program_counter_cell_count"] = -1,
            ["access_trace_callback_total"] = -1,
            ["access_trace_aggregated_total"] = -1,
            ["access_trace_outside_window_total"] = -1,
            ["disassembly_trace_start_cycle"] = -1,
            ["disassembly_trace_end_cycle"] = -1,
            ["disassembly_trace_send_count"] = -1,
            ["disassembly_trace_receive_count"] = -1,
            ["disassembly_trace_i2c_count"] = -1,
            ["disassembly_trace_led_command_send_count"] = -1,
            ["disassembly_trace_led_command_receive_count"] = -1,
            ["disassembly_trace_service_event_count"] = -1,
            ["disassembly_trace_avr_write_count"] = -1,
            ["disassembly_trace_avr_write_dropped_count"] = -1,
        };
        var text = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scenario"] = scenario,
            ["firmware_sha256"] = Hash(firmware),
            ["gdfs_sha256"] = Hash(gdfs),
            ["modem_sha256"] = Hash(modem),
            ["sim_identity"] = SimIdentity,
            ["idle_hash"] = string.Empty,
            ["menu_hash"] = string.Empty,
            ["service_tests_hash"] = string.Empty,
            ["selected_hash"] = string.Empty,
            ["window_end_hash"] = string.Empty,
            ["post_exit_hash"] = string.Empty,
            ["observation_setup"] = accessTracePath is null
                ? "display-i2c-transaction-count"
                : "display-i2c-transaction-count+aggregate-access-trace-v1",
            ["access_trace_path"] = string.Empty,
            ["access_trace_sha256"] = string.Empty,
            ["access_trace_schema"] = string.Empty,
        };
        var timeline = new List<string>();
        string? scenarioDirectory = writeArtifacts
            ? Path.Combine(ArtifactDirectory, scenario)
            : null;
        if (scenarioDirectory is not null)
        {
            Directory.CreateDirectory(scenarioDirectory);
        }

        using var machine = new MiaMachine(
            firmware.ToArray(),
            gdfs.ToArray(),
            modem.ToArray(),
            sim.ToArray(),
            virtualSim: true,
            powerPressedInitially: true,
            powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

        void Record(string action, string detail)
        {
            timeline.Add(
                $"{timeline.Count + 1}\t{machine.Cycles}\t{scenario}\t{action}\t" +
                $"{detail}\t{machine.FrameVersion}\t{FrameHash(machine)}");
        }

        void SaveCurrentFrame(string fileName) =>
            SaveFrame(
                scenarioDirectory,
                fileName,
                machine.Frame.Span);

        void Press(
            (string Name, string Symbol, byte ScanMask, byte RowMask,
                byte? SecondaryScanMask, bool Power) contact,
            long postCycles)
        {
            long downCycle = machine.Cycles;
            InteractiveKeypadTransition down = contact.Power
                ? machine.SetPowerKey(pressed: true)
                : machine.SetKey(
                    contact.ScanMask,
                    contact.RowMask,
                    contact.SecondaryScanMask,
                    pressed: true);
            Record($"key-{contact.Name}-down", down.ToString());
            Require(
                machine.Cycles == downCycle,
                $"The {contact.Name} down call advanced guest cycles.");
            RunUntilAtLeast(machine, downCycle + KeyDownCycleBudget);

            var deferredReleaseApplied = 0;
            void OnDeferredRelease(InteractiveKeypadContact released)
            {
                if (released.ScanMask == contact.ScanMask &&
                    released.RowMask == contact.RowMask &&
                    released.SecondaryScanMask == contact.SecondaryScanMask &&
                    released.Power == contact.Power)
                {
                    Volatile.Write(ref deferredReleaseApplied, 1);
                }
            }

            machine.InteractiveInput.DeferredReleaseApplied += OnDeferredRelease;
            try
            {
                InteractiveKeypadTransition up = contact.Power
                    ? machine.SetPowerKey(pressed: false)
                    : machine.SetKey(
                        contact.ScanMask,
                        contact.RowMask,
                        contact.SecondaryScanMask,
                        pressed: false);
                Record($"key-{contact.Name}-up", up.ToString());
                if (up == InteractiveKeypadTransition.Deferred)
                {
                    long deadline = machine.Cycles + 20_000_000;
                    while (Volatile.Read(ref deferredReleaseApplied) == 0 &&
                           machine.Cycles < deadline)
                    {
                        RunSome(machine);
                    }
                    Require(
                        Volatile.Read(ref deferredReleaseApplied) != 0,
                        $"Firmware did not scan the {contact.Name} release.");
                    Record($"key-{contact.Name}-release-applied", "native-scan");
                }
            }
            finally
            {
                machine.InteractiveInput.DeferredReleaseApplied -= OnDeferredRelease;
            }
            RunUntilAtLeast(machine, machine.Cycles + postCycles);
        }

        var right = (
            Name: "right", Symbol: ">", ScanMask: (byte)0x0e,
            RowMask: (byte)0x10, SecondaryScanMask: (byte?)0x07, Power: false);
        var left = (
            Name: "left", Symbol: "<", ScanMask: (byte)0x0d,
            RowMask: (byte)0x10, SecondaryScanMask: (byte?)0x0b, Power: false);
        var star = (
            Name: "star", Symbol: "*", ScanMask: (byte)0x07,
            RowMask: (byte)0x08, SecondaryScanMask: (byte?)null, Power: false);
        var down = (
            Name: "down", Symbol: "v", ScanMask: (byte)0x0e,
            RowMask: (byte)0x10, SecondaryScanMask: (byte?)0x0d, Power: false);
        var center = (
            Name: "center", Symbol: "o", ScanMask: (byte)0x0f,
            RowMask: (byte)0x08, SecondaryScanMask: (byte?)null, Power: false);
        int observedFrameVersion = machine.FrameVersion;
        long bootEnd = machine.ExecutedInstructions + BootInstructionBudget;
        while (!machine.IsStopped && machine.ExecutedInstructions < bootEnd)
        {
            RunSome(machine);
            if (machine.FrameVersion == observedFrameVersion)
            {
                continue;
            }
            observedFrameVersion = machine.FrameVersion;
            if (FrameHash(machine) == StandbyHash)
            {
                numbers["idle_cycle"] = machine.Cycles;
                numbers["idle_instruction"] = machine.ExecutedInstructions;
                break;
            }
        }
        Require(
            numbers["idle_cycle"] >= 0,
            $"{scenario}: native standby was not reached within the boot budget.");
        text["idle_hash"] = FrameHash(machine);
        Record("idle-reached", "native-frame");
        SaveCurrentFrame("idle");

        int stableFrameVersion = machine.FrameVersion;
        RunUntilAtLeast(machine, machine.Cycles + IdleStabilityCycleBudget);
        numbers["idle_stable_cycle"] = machine.Cycles;
        numbers["idle_stable_instruction"] = machine.ExecutedInstructions;
        Require(
            machine.FrameVersion == stableFrameVersion &&
            FrameHash(machine) == StandbyHash,
            $"{scenario}: native standby did not remain stable.");
        Record("idle-stable", $"cycles={IdleStabilityCycleBudget}");
        if (finalStage == 1)
        {
            WriteTimeline(scenarioDirectory, timeline);
            return (numbers, text, Reconciliation: null);
        }

        var sequence = new[] { right, star, left, left, star, left, star };
        Require(
            string.Concat(sequence.Select(contact => contact.Symbol)) ==
                ExactServiceSequence,
            "The configured service-menu sequence is not exact.");
        for (var sequenceIndex = 0;
             sequenceIndex < sequence.Length;
             sequenceIndex++)
        {
            var contact = sequence[sequenceIndex];
            Press(
                contact,
                sequenceIndex == sequence.Length - 1
                    ? MenuSettleCycleBudget
                    : KeyPostCycleBudget);
        }
        text["menu_hash"] = FrameHash(machine);
        numbers["menu_cycle"] = machine.Cycles;
        numbers["menu_instruction"] = machine.ExecutedInstructions;
        Require(
            text["menu_hash"] == ServiceMenuHash,
            $"{scenario}: native service menu fingerprint was not observed.");
        Record("service-menu-open", $"sequence={ExactServiceSequence}");
        SaveCurrentFrame("service-menu");
        if (finalStage == 2)
        {
            WriteTimeline(scenarioDirectory, timeline);
            return (numbers, text, Reconciliation: null);
        }

        Press(down, KeyPostCycleBudget);
        Press(down, KeyPostCycleBudget);
        Press(center, MenuSettleCycleBudget);
        text["service_tests_hash"] = FrameHash(machine);
        Require(
            text["service_tests_hash"] == ServiceTestsHash,
            $"{scenario}: native Service tests list was not observed.");
        Record("service-tests-open", "native-center-key");
        SaveCurrentFrame("service-tests");

        Press(down, KeyPostCycleBudget);
        text["selected_hash"] = FrameHash(machine);
        numbers["selected_cycle"] = machine.Cycles;
        numbers["selected_instruction"] = machine.ExecutedInstructions;
        Require(
            text["selected_hash"] == LedSelectedHash,
            $"{scenario}: LED/Illumination row was not selected.");
        Record("led-illumination-selected", "native-down-key");
        SaveCurrentFrame("led-illumination-selected");
        Require(
            machine.Cycles < ObservationStartCycle,
            $"{scenario}: navigation overran the fixed observation start cycle.");

        long callbackCount = 0;
        long callbackFirstCycle = -1;
        long callbackLastCycle = -1;
        long ledTestCallbackCount = 0;
        long ledSelectedCallbackCount = 0;
        Action<byte, byte[]> observationCallback = (_, _) =>
        {
            long cycle = machine.DisplayI2c.LastWriteCycle;
            long count = Interlocked.Increment(ref callbackCount);
            if (count == 1)
            {
                Interlocked.Exchange(ref callbackFirstCycle, cycle);
            }
            Interlocked.Exchange(ref callbackLastCycle, cycle);
            string frameHash = FrameHash(machine);
            if (frameHash == LedTestHash)
            {
                Interlocked.Increment(ref ledTestCallbackCount);
            }
            else if (frameHash == LedSelectedHash)
            {
                Interlocked.Increment(ref ledSelectedCallbackCount);
            }
        };
        byte[]? windowEndFrame = null;
        AccessTraceSession? accessTraceSession = null;
        IReadOnlyList<AccessTraceReconciliationSnapshot>? accessReconciliation =
            null;
        DelegateExecutionObserver? disassemblyObserver = null;
        CpuDataWriteObserver? disassemblyWriteObserver = null;
        Action<byte, byte[]>? disassemblyI2cCallback = null;
        var disassemblyI2cRequestSends = new List<LedServiceSignalEvent>();
        var disassemblyI2cRequestReceives = new List<LedServiceSignalEvent>();
        var disassemblyLedCommandSends = new List<LedServiceSignalEvent>();
        var disassemblyLedCommandReceives = new List<LedServiceSignalEvent>();
        var disassemblyI2cTransactions = new List<LedServiceI2cTransaction>();
        var disassemblyAvrWrites = new List<LedServiceAvrWrite>();
        var disassemblyServiceEvents = new List<LedServiceExecutionEvent>();
        long disassemblyAvrWriteDroppedCount = 0;
        var disassemblyTraceStopped = false;
        var startBoundaryFired = 0;
        var endBoundaryFired = 0;
        string? scheduledFailure = null;
        long windowEndCycle = ObservationStartCycle + ObservationWindowCycleBudget;

        machine.Clock.ScheduleAt(() =>
        {
            try
            {
                Require(
                    FrameHash(machine) == LedSelectedHash,
                    $"{scenario}: selected row changed before observation start.");
                machine.DisplayI2c.TransactionCompleted += observationCallback;
                numbers["attach_cycle"] = machine.Cycles;
                numbers["attach_ordinal"] = timeline.Count + 1;
                numbers["window_start_cycle"] = machine.Cycles;
                numbers["window_start_instruction"] = machine.ExecutedInstructions;
                Record("observation-callback-attach", "display-i2c-aggregate");
                if (disassemblyTracePath is not null)
                {
                    machine.Diagnostics.DisableFirmwareFastPaths();
                    disassemblyObserver = new(
                        tracedMachine =>
                        {
                            if (disassemblyTraceStopped)
                            {
                                return null;
                            }
                            var cpu = tracedMachine.Cpu;
                            if (IsLedServiceTracePc(cpu.PC))
                            {
                                int stackLength = Math.Min(
                                    24,
                                    cpu.Data.Length - cpu.SP - 1);
                                disassemblyServiceEvents.Add((
                                    tracedMachine.Cycles,
                                    cpu.PC,
                                    cpu.Data[0x00f606],
                                    Convert.ToHexString(
                                        cpu.Data.AsSpan(0x0273ba, 9)),
                                    Convert.ToHexString(
                                        cpu.Data.AsSpan(0x028475, 14)),
                                    Convert.ToHexString(
                                        cpu.Data.AsSpan(0, 32)),
                                    Convert.ToHexString(cpu.Data.AsSpan(
                                        cpu.SP + 1,
                                        stackLength))));
                            }
                            if (cpu.PC == 0x001838 &&
                                TryCaptureLedServiceSignal(
                                    tracedMachine,
                                    process: cpu.ReadData(0x00f606),
                                    peerProcess: cpu.Data[20],
                                    returnPc: ReadAvrReturnPc(cpu),
                                    out LedServiceSignalEvent sent))
                            {
                                if (sent.PeerProcess == 0x21 &&
                                    sent.Signal == 0x07ac)
                                {
                                    disassemblyI2cRequestSends.Add(sent);
                                }
                                else if (sent.Signal == 0x0bd7)
                                {
                                    disassemblyLedCommandSends.Add(sent);
                                }
                            }
                            else if (cpu.PC == 0x001912 &&
                                     TryCaptureLedServiceSignal(
                                         tracedMachine,
                                         process: cpu.ReadData(0x00f606),
                                         peerProcess: ReadLedSignalSource(cpu),
                                         returnPc: ReadAvrReturnPc(cpu),
                                         out LedServiceSignalEvent received))
                            {
                                if (received.Process == 0x21 &&
                                    received.Signal == 0x07ac)
                                {
                                    disassemblyI2cRequestReceives.Add(received);
                                }
                                else if (received.Signal == 0x0bd7)
                                {
                                    disassemblyLedCommandReceives.Add(received);
                                }
                            }
                            return null;
                        },
                        (_, _, _) => { });
                    machine.Diagnostics.InspectAvr(cpu =>
                    {
                        long avrToAsicOffset = checked(
                            machine.Cycles -
                            AccessTraceClockConversion.AvrToAsic(cpu.Cycles));
                        disassemblyWriteObserver = (
                            address,
                            oldValue,
                            newValue,
                            mask,
                            hookConsumed) =>
                        {
                            if (disassemblyTraceStopped)
                            {
                                return;
                            }
                            byte effectiveValue = hookConsumed
                                ? newValue
                                : (byte)((oldValue & ~mask) |
                                    (newValue & mask));
                            if (effectiveValue != 0xa4 &&
                                effectiveValue != 0x87 &&
                                address is not 0x0890 and not 0x0895 and
                                not 0x0a40 and not 0x0a44 and
                                not 0x0a4a and not 0x0a4d)
                            {
                                return;
                            }
                            const int MaximumCapturedWrites = 100_000;
                            if (disassemblyAvrWrites.Count >=
                                MaximumCapturedWrites)
                            {
                                disassemblyAvrWriteDroppedCount++;
                                return;
                            }
                            int stackLength = Math.Min(
                                24,
                                cpu.Data.Length - cpu.SP - 1);
                            disassemblyAvrWrites.Add((
                                checked(
                                    AccessTraceClockConversion.AvrToAsic(
                                        cpu.Cycles) + avrToAsicOffset),
                                cpu.Cycles,
                                cpu.PC,
                                cpu.SP,
                                cpu.Data[0x00f606],
                                address,
                                oldValue,
                                newValue,
                                mask,
                                effectiveValue,
                                hookConsumed,
                                Convert.ToHexString(cpu.Data.AsSpan(0, 32)),
                                Convert.ToHexString(cpu.Data.AsSpan(
                                    cpu.SP + 1,
                                    stackLength))));
                        };
                        cpu.DataWriteObserver =
                            disassemblyWriteObserver + cpu.DataWriteObserver;
                        return true;
                    });
                    disassemblyI2cCallback = (address, payload) =>
                    {
                        if (!disassemblyTraceStopped)
                        {
                            disassemblyI2cTransactions.Add((
                                machine.Cycles,
                                machine.ExecutedInstructions,
                                machine.Cpu.PC,
                                machine.Cpu.ReadData(0x00f606),
                                address,
                                Convert.ToHexString(payload)));
                        }
                    };
                    machine.PrimaryI2c.TransactionCompleted +=
                        disassemblyI2cCallback;
                    machine.ExecutionObserver = disassemblyObserver;
                    numbers["disassembly_trace_start_cycle"] = machine.Cycles;
                    Record(
                        "disassembly-trace-attach",
                        "AVR-send/receive+primary-i2c");
                    machine.Clock.ScheduleAt(() =>
                    {
                        disassemblyTraceStopped = true;
                        machine.ExecutionObserver = null;
                        if (disassemblyWriteObserver is not null)
                        {
                            machine.Diagnostics.InspectAvr(cpu =>
                            {
                                cpu.DataWriteObserver =
                                    (CpuDataWriteObserver?)Delegate.Remove(
                                        cpu.DataWriteObserver,
                                        disassemblyWriteObserver);
                                return true;
                            });
                        }
                        if (disassemblyI2cCallback is not null)
                        {
                            machine.PrimaryI2c.TransactionCompleted -=
                                disassemblyI2cCallback;
                        }
                        numbers["disassembly_trace_end_cycle"] = machine.Cycles;
                        Record("disassembly-trace-detach", "bounded-native-window");
                    }, ObservationStartCycle + DisassemblyTraceCycleBudget);
                }
                if (accessTracePath is not null)
                {
                    numbers["access_trace_attach_cycle"] = machine.Cycles;
                    numbers["access_trace_attach_ordinal"] = timeline.Count + 1;
                    accessTraceSession = new(
                        machine,
                        ObservationStartCycle,
                        windowEndCycle);
                    Require(
                        machine.Cycles == ObservationStartCycle,
                        $"{scenario}: access-trace attach advanced guest cycles.");
                    Record("access-trace-attach", "aggregate-only");
                    if (triggerTest)
                    {
                        numbers["trigger_cycle"] = machine.Cycles;
                        numbers["trigger_ordinal"] = timeline.Count + 1;
                        numbers["center_trigger_count"]++;
                        numbers["window_key_event_count"]++;
                        InteractiveKeypadTransition result = machine.SetKey(
                            center.ScanMask,
                            center.RowMask,
                            center.SecondaryScanMask,
                            pressed: true);
                        Require(
                            machine.Cycles == ObservationStartCycle,
                            $"{scenario}: Center-down advanced guest cycles.");
                        Record("key-center-trigger-down", result.ToString());
                        ScheduleTriggerRelease();
                    }
                    else
                    {
                        numbers["control_hold_begin_cycle"] = machine.Cycles;
                        numbers["control_hold_begin_ordinal"] = timeline.Count + 1;
                        Record("control-hold-begin", "center-not-pressed");
                    }
                }
                else
                {
                    if (triggerTest)
                    {
                        numbers["trigger_cycle"] = machine.Cycles;
                        numbers["trigger_ordinal"] = timeline.Count + 1;
                        numbers["center_trigger_count"]++;
                        numbers["window_key_event_count"]++;
                        InteractiveKeypadTransition result = machine.SetKey(
                            center.ScanMask,
                            center.RowMask,
                            center.SecondaryScanMask,
                            pressed: true);
                        Record("key-center-trigger-down", result.ToString());
                        ScheduleTriggerRelease();
                    }
                    else
                    {
                        numbers["control_hold_begin_cycle"] = machine.Cycles;
                        numbers["control_hold_begin_ordinal"] = timeline.Count + 1;
                        Record("control-hold-begin", "center-not-pressed");
                    }
                }
                Volatile.Write(ref startBoundaryFired, 1);

                void ScheduleTriggerRelease() => machine.Clock.ScheduleAt(() =>
                {
                    numbers["window_key_event_count"]++;
                    InteractiveKeypadTransition release = machine.SetKey(
                        center.ScanMask,
                        center.RowMask,
                        center.SecondaryScanMask,
                        pressed: false);
                    Record("key-center-trigger-up", release.ToString());
                }, ObservationStartCycle + TriggerKeyDownCycleBudget);
            }
            catch (InvalidOperationException error)
            {
                scheduledFailure = error.Message;
                Volatile.Write(ref startBoundaryFired, 1);
            }
        }, ObservationStartCycle);

        machine.Clock.ScheduleAt(() =>
        {
            try
            {
                machine.DisplayI2c.TransactionCompleted -= observationCallback;
                numbers["detach_cycle"] = machine.Cycles;
                numbers["detach_ordinal"] = timeline.Count + 1;
                numbers["window_end_cycle"] = machine.Cycles;
                numbers["window_end_instruction"] = machine.ExecutedInstructions;
                windowEndFrame = machine.Frame.ToArray();
                Record("observation-callback-detach", "before-exit");
                if (accessTracePath is not null)
                {
                    AccessTraceSession activeTrace = accessTraceSession ??
                        throw new InvalidOperationException(
                            $"{scenario}: access trace was not attached.");
                    numbers["access_trace_stop_call_cycle"] = machine.Cycles;
                    numbers["access_trace_stop_ordinal"] = timeline.Count + 1;
                    if (triggerTest)
                    {
                        numbers["exit_cycle"] = machine.Cycles;
                        numbers["exit_ordinal"] = timeline.Count + 2;
                        activeTrace.Stop(windowEndCycle);
                        Record("access-trace-stop", "aggregate-only");
                        InteractiveKeypadTransition result = machine.SetPowerKey(
                            pressed: true);
                        Require(
                            machine.Cycles == windowEndCycle,
                            $"{scenario}: trace stop or exit-down advanced guest cycles.");
                        Record("key-exit-down", result.ToString());
                    }
                    else
                    {
                        numbers["control_hold_end_cycle"] = machine.Cycles;
                        numbers["control_hold_end_ordinal"] = timeline.Count + 2;
                        activeTrace.Stop(windowEndCycle);
                        Record("access-trace-stop", "aggregate-only");
                        Record("control-hold-end", "center-not-pressed");
                    }
                }
                else
                {
                    if (triggerTest)
                    {
                        numbers["exit_cycle"] = machine.Cycles;
                        numbers["exit_ordinal"] = timeline.Count + 1;
                        InteractiveKeypadTransition result = machine.SetPowerKey(
                            pressed: true);
                        Record("key-exit-down", result.ToString());
                    }
                    else
                    {
                        numbers["control_hold_end_cycle"] = machine.Cycles;
                        numbers["control_hold_end_ordinal"] = timeline.Count + 1;
                        Record("control-hold-end", "center-not-pressed");
                    }
                }
                Volatile.Write(ref endBoundaryFired, 1);
            }
            catch (InvalidOperationException error)
            {
                scheduledFailure = error.Message;
                Volatile.Write(ref endBoundaryFired, 1);
            }
        }, windowEndCycle);

        long windowInstructionEnd =
            machine.ExecutedInstructions + ObservationInstructionSafetyBudget;
        while (Volatile.Read(ref endBoundaryFired) == 0 &&
               !machine.IsStopped &&
               machine.ExecutedInstructions < windowInstructionEnd)
        {
            RunSome(machine);
        }
        if (Volatile.Read(ref startBoundaryFired) == 0 ||
            Volatile.Read(ref endBoundaryFired) == 0 ||
            scheduledFailure is not null)
        {
            accessTraceSession?.Stop();
        }
        Require(
            Volatile.Read(ref startBoundaryFired) != 0 &&
            Volatile.Read(ref endBoundaryFired) != 0,
            $"{scenario}: scheduled observation boundaries did not execute.");
        Require(
            scheduledFailure is null,
            $"{scenario}: scheduled boundary failed: {scheduledFailure}");
        Require(
            windowEndFrame is not null,
            $"{scenario}: no end-of-window framebuffer was retained.");

        numbers["observation_callback_count"] =
            Interlocked.Read(ref callbackCount);
        numbers["observation_first_cycle"] =
            Interlocked.Read(ref callbackFirstCycle);
        numbers["observation_last_cycle"] =
            Interlocked.Read(ref callbackLastCycle);
        numbers["led_test_callback_count"] =
            Interlocked.Read(ref ledTestCallbackCount);
        numbers["led_selected_callback_count"] =
            Interlocked.Read(ref ledSelectedCallbackCount);
        text["window_end_hash"] = Hash(windowEndFrame);
        SaveFrame(scenarioDirectory, "window-end", windowEndFrame);
        WriteTimeline(scenarioDirectory, timeline);

        Require(
            numbers["window_start_cycle"] == ObservationStartCycle &&
            numbers["window_end_cycle"] == windowEndCycle,
            $"{scenario}: observation window missed an exact cycle boundary.");
        Require(
            numbers["observation_first_cycle"] < 0 ||
            numbers["observation_first_cycle"] >= ObservationStartCycle,
            $"{scenario}: observation contains pre-trigger activity.");
        Require(
            numbers["observation_last_cycle"] < 0 ||
            numbers["observation_last_cycle"] <= windowEndCycle,
            $"{scenario}: observation contains post-boundary activity.");

        if (triggerTest)
        {
            Require(
                text["window_end_hash"] == LedTestHash,
                $"{scenario}: native LED/Illumination test did not remain open " +
                $"(end={text["window_end_hash"]}, callbacks=" +
                $"{numbers["observation_callback_count"]}, test-callbacks=" +
                $"{numbers["led_test_callback_count"]}, selected-callbacks=" +
                $"{numbers["led_selected_callback_count"]}).");
            RunUntilAtLeast(machine, machine.Cycles + KeyDownCycleBudget);
            InteractiveKeypadTransition exitRelease = machine.SetPowerKey(
                pressed: false);
            Record("key-exit-up", exitRelease.ToString());
            RunUntilAtLeast(machine, machine.Cycles + MenuSettleCycleBudget);
            text["post_exit_hash"] = FrameHash(machine);
            numbers["callback_count_after_exit"] =
                Interlocked.Read(ref callbackCount);
            Require(
                text["post_exit_hash"] == LedSelectedHash,
                $"{scenario}: native exit did not return to the selected row.");
            Require(
                numbers["callback_count_after_exit"] ==
                    numbers["observation_callback_count"],
                $"{scenario}: callback observed exit handling or later frames.");
            Record("post-exit-led-illumination-selected", "outside-observation");
            SaveCurrentFrame("post-exit-led-illumination-selected");
        }
        else
        {
            text["post_exit_hash"] = FrameHash(machine);
            numbers["callback_count_after_exit"] =
                Interlocked.Read(ref callbackCount);
            Require(
                text["window_end_hash"] == LedSelectedHash &&
                text["post_exit_hash"] == LedSelectedHash,
                $"{scenario}: matched control did not hold the selected row.");
            Require(
                numbers["center_trigger_count"] == 0,
                $"{scenario}: matched control sent the Center trigger.");
            Require(
                numbers["window_key_event_count"] == 0,
                $"{scenario}: matched control sent a key inside the window.");
            int firstInsideIndex = checked((int)numbers["attach_ordinal"]);
            int insideCount = checked((int)(
                numbers["detach_ordinal"] -
                numbers["attach_ordinal"] -
                1));
            bool recordedKeyInsideWindow = timeline
                .Skip(firstInsideIndex)
                .Take(insideCount)
                .Select(line => line.Split('\t')[3])
                .Any(action => action.StartsWith("key-", StringComparison.Ordinal));
            Require(
                !recordedKeyInsideWindow,
                $"{scenario}: action timeline contains a key strictly inside the window.");
        }

        if (accessTracePath is not null)
        {
            AccessTraceSession completedTrace = accessTraceSession ??
                throw new InvalidOperationException(
                    $"{scenario}: access-trace session is missing after the window.");
            accessReconciliation = SummarizeAccessTrace(
                completedTrace,
                scenario,
                windowEndCycle,
                numbers,
                text);
            Require(
                !File.Exists(accessTracePath),
                $"{scenario}: refusing to overwrite {accessTracePath}.");
            completedTrace.WriteJson(accessTracePath);
            var traceFile = new FileInfo(accessTracePath);
            Require(
                traceFile.Length > 0,
                $"{scenario}: access-trace file is empty.");
            text["access_trace_path"] = accessTracePath;
            text["access_trace_sha256"] = HashFile(accessTracePath);
            numbers["access_trace_compressed_bytes"] = traceFile.Length;
            completedTrace.Dispose();
        }

        if (disassemblyTracePath is not null)
        {
            _ = disassemblyObserver ?? throw new InvalidOperationException(
                    $"{scenario}: disassembly observer was not attached.");
            Require(
                disassemblyTraceStopped,
                $"{scenario}: disassembly observer did not reach its stop boundary.");
            Require(
                !File.Exists(disassemblyTracePath),
                $"{scenario}: refusing to overwrite {disassemblyTracePath}.");
            string? parent = Path.GetDirectoryName(
                Path.GetFullPath(disassemblyTracePath));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            WriteLedServiceDisassemblyTrace(
                disassemblyTracePath,
                firmwareSha256: text["firmware_sha256"],
                startCycle: numbers["disassembly_trace_start_cycle"],
                endCycle: numbers["disassembly_trace_end_cycle"],
                disassemblyI2cRequestSends,
                disassemblyI2cRequestReceives,
                disassemblyLedCommandSends,
                disassemblyLedCommandReceives,
                disassemblyI2cTransactions,
                disassemblyServiceEvents,
                disassemblyAvrWrites,
                disassemblyAvrWriteDroppedCount);
            numbers["disassembly_trace_send_count"] =
                disassemblyI2cRequestSends.Count;
            numbers["disassembly_trace_receive_count"] =
                disassemblyI2cRequestReceives.Count;
            numbers["disassembly_trace_i2c_count"] =
                disassemblyI2cTransactions.Count;
            numbers["disassembly_trace_led_command_send_count"] =
                disassemblyLedCommandSends.Count;
            numbers["disassembly_trace_led_command_receive_count"] =
                disassemblyLedCommandReceives.Count;
            numbers["disassembly_trace_service_event_count"] =
                disassemblyServiceEvents.Count;
            numbers["disassembly_trace_avr_write_count"] =
                disassemblyAvrWrites.Count;
            numbers["disassembly_trace_avr_write_dropped_count"] =
                disassemblyAvrWriteDroppedCount;
        }

        WriteTimeline(scenarioDirectory, timeline);
        return (numbers, text, accessReconciliation);
    }

    IReadOnlyList<AccessTraceReconciliationSnapshot> SummarizeAccessTrace(
        AccessTraceSession session,
        string scenario,
        long expectedEndCycle,
        Dictionary<string, long> numbers,
        Dictionary<string, string> text)
    {
        AccessTraceSnapshot snapshot = session.Snapshot();
        Require(
            snapshot.Schema == AccessTraceSnapshot.CurrentSchema,
            $"{scenario}: access-trace schema is unexpected.");
        Require(
            snapshot.Window.StartCycle == ObservationStartCycle &&
            snapshot.Window.EndCycleExclusive == expectedEndCycle &&
            snapshot.Window.DurationCycles == ObservationWindowCycleBudget &&
            snapshot.Window.BinCount == AccessTraceWindow.RequiredBinCount,
            $"{scenario}: access-trace window metadata is not exact.");
        Require(
            snapshot.Capture.Stopped &&
            snapshot.Capture.StoppedAtCycle == expectedEndCycle,
            $"{scenario}: access trace did not stop at cycle {expectedEndCycle}.");
        Require(
            snapshot.Storage.AddressCount == snapshot.Addresses.Count &&
            snapshot.Storage.AddressCount > 0 &&
            snapshot.Storage.TimeBinCellCount >= 0 &&
            snapshot.Storage.BinValueCellCount >= 0 &&
            snapshot.Storage.ValueHistogramCellCount >= 0 &&
            snapshot.Storage.TransitionCellCount >= 0 &&
            snapshot.Storage.ProgramCounterCellCount >= 0,
            $"{scenario}: access-trace storage metrics are inconsistent.");
        Require(
            snapshot.Reconciliation.Count == 6,
            $"{scenario}: access trace lacks the six bus/operation totals.");

        long callbackTotal = 0;
        long aggregatedTotal = 0;
        long outsideWindowTotal = 0;
        foreach (AccessTraceReconciliationSnapshot item in
                 snapshot.Reconciliation)
        {
            Require(
                item.CallbackCount >= 0 &&
                item.AggregatedCount >= 0 &&
                item.OutsideWindowCount >= 0,
                $"{scenario}: access-trace reconciliation is negative.");
            Require(
                item.CallbackCount == checked(
                    item.AggregatedCount + item.OutsideWindowCount),
                $"{scenario}: {item.Bus} {item.Operation} access counts do not " +
                "reconcile.");
            callbackTotal = checked(callbackTotal + item.CallbackCount);
            aggregatedTotal = checked(aggregatedTotal + item.AggregatedCount);
            outsideWindowTotal = checked(
                outsideWindowTotal + item.OutsideWindowCount);
        }
        Require(
            aggregatedTotal > 0,
            $"{scenario}: access trace contains no in-window accesses.");
        Require(
            outsideWindowTotal == 0,
            $"{scenario}: access trace observed callbacks outside its window.");

        long stoppedCycle = snapshot.Capture.StoppedAtCycle ??
            throw new InvalidOperationException(
                $"{scenario}: stopped access trace lacks its stop cycle.");
        text["access_trace_schema"] = snapshot.Schema;
        numbers["access_trace_start_cycle"] = snapshot.Window.StartCycle;
        numbers["access_trace_end_cycle_exclusive"] =
            snapshot.Window.EndCycleExclusive;
        numbers["access_trace_duration_cycles"] = snapshot.Window.DurationCycles;
        numbers["access_trace_bin_count"] = snapshot.Window.BinCount;
        numbers["access_trace_stopped_cycle"] = stoppedCycle;
        numbers["access_trace_address_count"] = snapshot.Storage.AddressCount;
        numbers["access_trace_time_bin_cell_count"] =
            snapshot.Storage.TimeBinCellCount;
        numbers["access_trace_bin_value_cell_count"] =
            snapshot.Storage.BinValueCellCount;
        numbers["access_trace_value_histogram_cell_count"] =
            snapshot.Storage.ValueHistogramCellCount;
        numbers["access_trace_transition_cell_count"] =
            snapshot.Storage.TransitionCellCount;
        numbers["access_trace_program_counter_cell_count"] =
            snapshot.Storage.ProgramCounterCellCount;
        numbers["access_trace_callback_total"] = callbackTotal;
        numbers["access_trace_aggregated_total"] = aggregatedTotal;
        numbers["access_trace_outside_window_total"] = outsideWindowTotal;
        return snapshot.Reconciliation.ToArray();
    }

    void VerifyAccessCapturePair(
        string pairId,
        ProbeScenarioResult service,
        ProbeScenarioResult control)
    {
        Require(
            service.Reconciliation is not null &&
            control.Reconciliation is not null,
            $"{pairId}: service or control access capture is missing.");
        long endCycle = ObservationStartCycle + ObservationWindowCycleBudget;

        Require(
            service.Numbers["access_trace_attach_cycle"] ==
                ObservationStartCycle &&
            service.Numbers["trigger_cycle"] == ObservationStartCycle &&
            service.Numbers["trigger_ordinal"] ==
                service.Numbers["access_trace_attach_ordinal"] + 1,
            $"{pairId}: service trace did not attach immediately before Center-down.");
        Require(
            service.Numbers["access_trace_stop_call_cycle"] == endCycle &&
            service.Numbers["exit_cycle"] == endCycle &&
            service.Numbers["exit_ordinal"] ==
                service.Numbers["access_trace_stop_ordinal"] + 1,
            $"{pairId}: service trace did not stop immediately before Power-down.");
        Require(
            control.Numbers["access_trace_attach_cycle"] ==
                ObservationStartCycle &&
            control.Numbers["control_hold_begin_cycle"] ==
                ObservationStartCycle &&
            control.Numbers["control_hold_begin_ordinal"] ==
                control.Numbers["access_trace_attach_ordinal"] + 1,
            $"{pairId}: control trace did not attach immediately before its hold.");
        Require(
            control.Numbers["access_trace_stop_call_cycle"] == endCycle &&
            control.Numbers["control_hold_end_cycle"] == endCycle &&
            control.Numbers["control_hold_end_ordinal"] ==
                control.Numbers["access_trace_stop_ordinal"] + 1,
            $"{pairId}: control trace did not stop immediately before hold end.");
        Require(
            control.Numbers["center_trigger_count"] == 0 &&
            control.Numbers["window_key_event_count"] == 0,
            $"{pairId}: control capture contains an in-window key event.");
        Require(
            service.Numbers["access_trace_start_cycle"] ==
                control.Numbers["access_trace_start_cycle"] &&
            service.Numbers["access_trace_end_cycle_exclusive"] ==
                control.Numbers["access_trace_end_cycle_exclusive"] &&
            service.Numbers["access_trace_duration_cycles"] ==
                control.Numbers["access_trace_duration_cycles"] &&
            service.Numbers["access_trace_stopped_cycle"] ==
                control.Numbers["access_trace_stopped_cycle"],
            $"{pairId}: service/control access windows do not match.");
    }

    void WriteAccessCaptureManifest(
        string manifestPath,
        IReadOnlyList<AccessCaptureRun> captures)
    {
        Require(
            captures.Count == AccessCapturePairCount * 2,
            "The access-capture manifest requires exactly six ordered runs.");
        AccessCaptureRun baseline = captures[0];
        string manifestDirectory = Path.GetFullPath(
            Path.GetDirectoryName(manifestPath) ?? ".");
        for (var index = 0; index < captures.Count; index++)
        {
            AccessCaptureRun capture = captures[index];
            string expectedPairId = $"pair-{index / 2 + 1:00}";
            string expectedScenario = index % 2 == 0 ? "service" : "control";
            Require(
                capture.Order == index + 1 &&
                capture.PairId == expectedPairId &&
                capture.Scenario == expectedScenario,
                "Access-capture order is not service/control alternating by pair.");
            string tracePath = capture.Text["access_trace_path"];
            Require(
                Path.GetFullPath(Path.GetDirectoryName(tracePath) ?? ".") ==
                    manifestDirectory &&
                tracePath.EndsWith(".json.gz", StringComparison.Ordinal) &&
                File.Exists(tracePath),
                $"{capture.PairId} {capture.Scenario}: trace path is invalid.");
            Require(
                capture.Text["access_trace_sha256"].Length == 64 &&
                new FileInfo(tracePath).Length ==
                    capture.Numbers["access_trace_compressed_bytes"],
                $"{capture.PairId} {capture.Scenario}: trace file metadata is stale.");

            foreach (string key in new[]
                     {
                         "firmware_sha256",
                         "gdfs_sha256",
                         "modem_sha256",
                         "sim_identity",
                         "idle_hash",
                         "menu_hash",
                         "service_tests_hash",
                         "selected_hash",
                         "observation_setup",
                     })
            {
                Require(
                    capture.Text[key] == baseline.Text[key],
                    $"Capture runs differ at {key}.");
            }
            foreach (string key in new[]
                    {
                        "boot_instruction_budget",
                        "idle_cycle",
                        "idle_stable_cycle",
                        "menu_cycle",
                        "selected_cycle",
                        "window_start_cycle",
                        "window_end_cycle",
                    })
            {
                Require(
                    capture.Numbers[key] == baseline.Numbers[key],
                    $"Capture runs differ at {key}.");
            }
        }

        using var output = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", AccessCaptureManifestSchema);
        writer.WriteNumber("pairCount", AccessCapturePairCount);
        writer.WriteNumber("runCount", captures.Count);
        writer.WriteString("runOrder", "service-then-control-per-pair");
        writer.WriteBoolean("retainsPerEventRecords", false);
        writer.WriteString(
            "traceSchema",
            baseline.Text["access_trace_schema"]);

        writer.WriteStartObject("inputs");
        writer.WriteStartObject("firmware");
        writer.WriteString("path", FirmwarePath);
        writer.WriteString("sha256", baseline.Text["firmware_sha256"]);
        writer.WriteEndObject();
        writer.WriteStartObject("gdfs");
        writer.WriteString("path", GdfsPath);
        writer.WriteString("sha256", baseline.Text["gdfs_sha256"]);
        writer.WriteEndObject();
        writer.WriteStartObject("modem");
        writer.WriteString("path", ModemPath);
        writer.WriteString("sha256", baseline.Text["modem_sha256"]);
        writer.WriteEndObject();
        writer.WriteString("simIdentity", baseline.Text["sim_identity"]);
        writer.WriteBoolean("virtualSim", true);
        writer.WriteEndObject();

        writer.WriteStartObject("machine");
        writer.WriteString(
            "coreScheduling",
            MiaCoreSchedulingMode.CoarseParallel.ToString());
        writer.WriteBoolean("powerPressedInitially", true);
        writer.WriteNumber(
            "powerReleaseCycle",
            MiaMachine.DefaultPowerKeyReleaseCycle);
        writer.WriteNumber("bootInstructionBudget", BootInstructionBudget);
        writer.WriteNumber(
            "observationInstructionSafetyBudget",
            ObservationInstructionSafetyBudget);
        writer.WriteString("inputMode", "native-keypad");
        writer.WriteString("serviceMenuSequence", ExactServiceSequence);
        writer.WriteString("observationSetup", baseline.Text["observation_setup"]);
        writer.WriteEndObject();

        writer.WriteStartObject("window");
        writer.WriteNumber("startCycle", ObservationStartCycle);
        writer.WriteNumber(
            "endCycleExclusive",
            ObservationStartCycle + ObservationWindowCycleBudget);
        writer.WriteNumber("durationCycles", ObservationWindowCycleBudget);
        writer.WriteNumber(
            "binCount",
            baseline.Numbers["access_trace_bin_count"]);
        writer.WriteEndObject();

        writer.WriteStartArray("runs");
        foreach (AccessCaptureRun capture in captures)
        {
            writer.WriteStartObject();
            writer.WriteNumber("order", capture.Order);
            writer.WriteString("pairId", capture.PairId);
            writer.WriteString("scenario", capture.Scenario);
            writer.WriteString("runId", capture.Text["scenario"]);

            writer.WriteStartObject("file");
            writer.WriteString(
                "path",
                Path.GetFileName(capture.Text["access_trace_path"]));
            writer.WriteString(
                "sha256",
                capture.Text["access_trace_sha256"]);
            writer.WriteNumber(
                "compressedBytes",
                capture.Numbers["access_trace_compressed_bytes"]);
            writer.WriteEndObject();

            writer.WriteStartObject("boundaries");
            writer.WriteNumber(
                "startCycle",
                capture.Numbers["access_trace_start_cycle"]);
            writer.WriteNumber(
                "endCycleExclusive",
                capture.Numbers["access_trace_end_cycle_exclusive"]);
            writer.WriteNumber(
                "durationCycles",
                capture.Numbers["access_trace_duration_cycles"]);
            writer.WriteNumber(
                "stoppedCycle",
                capture.Numbers["access_trace_stopped_cycle"]);
            writer.WriteNumber(
                "attachCycle",
                capture.Numbers["access_trace_attach_cycle"]);
            writer.WriteNumber(
                "triggerOrHoldCycle",
                capture.Scenario == "service"
                    ? capture.Numbers["trigger_cycle"]
                    : capture.Numbers["control_hold_begin_cycle"]);
            writer.WriteNumber(
                "stopCallCycle",
                capture.Numbers["access_trace_stop_call_cycle"]);
            writer.WriteNumber(
                "exitOrHoldEndCycle",
                capture.Scenario == "service"
                    ? capture.Numbers["exit_cycle"]
                    : capture.Numbers["control_hold_end_cycle"]);
            writer.WriteNumber(
                "attachOrdinal",
                capture.Numbers["access_trace_attach_ordinal"]);
            writer.WriteNumber(
                "triggerOrHoldOrdinal",
                capture.Scenario == "service"
                    ? capture.Numbers["trigger_ordinal"]
                    : capture.Numbers["control_hold_begin_ordinal"]);
            writer.WriteNumber(
                "stopOrdinal",
                capture.Numbers["access_trace_stop_ordinal"]);
            writer.WriteNumber(
                "exitOrHoldEndOrdinal",
                capture.Scenario == "service"
                    ? capture.Numbers["exit_ordinal"]
                    : capture.Numbers["control_hold_end_ordinal"]);
            writer.WriteEndObject();

            writer.WriteStartObject("nativePath");
            writer.WriteString(
                "idleFramebufferSha256",
                capture.Text["idle_hash"]);
            writer.WriteString(
                "menuFramebufferSha256",
                capture.Text["menu_hash"]);
            writer.WriteString(
                "serviceTestsFramebufferSha256",
                capture.Text["service_tests_hash"]);
            writer.WriteString(
                "selectedFramebufferSha256",
                capture.Text["selected_hash"]);
            writer.WriteString(
                "windowEndFramebufferSha256",
                capture.Text["window_end_hash"]);
            writer.WriteString(
                "postExitFramebufferSha256",
                capture.Text["post_exit_hash"]);
            writer.WriteNumber(
                "centerTriggerCount",
                capture.Numbers["center_trigger_count"]);
            writer.WriteNumber(
                "inWindowKeyEventCount",
                capture.Numbers["window_key_event_count"]);
            writer.WriteEndObject();

            writer.WriteStartObject("storage");
            writer.WriteNumber(
                "addressCount",
                capture.Numbers["access_trace_address_count"]);
            writer.WriteNumber(
                "timeBinCellCount",
                capture.Numbers["access_trace_time_bin_cell_count"]);
            writer.WriteNumber(
                "binValueCellCount",
                capture.Numbers["access_trace_bin_value_cell_count"]);
            writer.WriteNumber(
                "valueHistogramCellCount",
                capture.Numbers["access_trace_value_histogram_cell_count"]);
            writer.WriteNumber(
                "transitionCellCount",
                capture.Numbers["access_trace_transition_cell_count"]);
            writer.WriteNumber(
                "programCounterCellCount",
                capture.Numbers["access_trace_program_counter_cell_count"]);
            writer.WriteEndObject();

            writer.WriteStartObject("reconciliationTotals");
            writer.WriteNumber("rowCount", capture.Reconciliation.Count);
            writer.WriteNumber(
                "callbackCount",
                capture.Numbers["access_trace_callback_total"]);
            writer.WriteNumber(
                "aggregatedCount",
                capture.Numbers["access_trace_aggregated_total"]);
            writer.WriteNumber(
                "outsideWindowCount",
                capture.Numbers["access_trace_outside_window_total"]);
            writer.WriteEndObject();
            writer.WriteStartArray("reconciliation");
            foreach (AccessTraceReconciliationSnapshot item in
                     capture.Reconciliation)
            {
                writer.WriteStartObject();
                writer.WriteString("bus", FormatTraceBus(item.Bus));
                writer.WriteString(
                    "operation",
                    FormatTraceOperation(item.Operation));
                writer.WriteNumber("callbackCount", item.CallbackCount);
                writer.WriteNumber("aggregatedCount", item.AggregatedCount);
                writer.WriteNumber(
                    "outsideWindowCount",
                    item.OutsideWindowCount);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    void VerifyMatchedRuns(
        ProbeScenarioResult service,
        ProbeScenarioResult control)
    {
        foreach (string key in new[]
                 {
                     "firmware_sha256",
                     "gdfs_sha256",
                     "modem_sha256",
                     "sim_identity",
                     "idle_hash",
                     "menu_hash",
                     "service_tests_hash",
                     "selected_hash",
                     "observation_setup",
                 })
        {
            Require(
                service.Text[key] == control.Text[key],
                $"Matched run identity or native path differs at {key}.");
        }
        foreach (string key in new[]
                 {
                     "boot_instruction_budget",
                     "idle_cycle",
                     "idle_stable_cycle",
                     "menu_cycle",
                     "selected_cycle",
                     "window_start_cycle",
                     "window_end_cycle",
                 })
        {
            Require(
                service.Numbers[key] == control.Numbers[key],
                $"Matched run budget or cycle differs at {key}.");
        }
        Require(
            service.Numbers["window_end_cycle"] -
                service.Numbers["window_start_cycle"] ==
                ObservationWindowCycleBudget &&
            control.Numbers["window_end_cycle"] -
                control.Numbers["window_start_cycle"] ==
                ObservationWindowCycleBudget,
            "Matched observation durations are not exact.");
        Require(
            control.Numbers["center_trigger_count"] == 0,
            "Matched control contains a Center trigger.");
        Require(
            control.Numbers["window_key_event_count"] == 0,
            "Matched control contains a key event inside its window.");
    }

    void VerifyExactBoundaries(
        ProbeScenarioResult service,
        ProbeScenarioResult control)
    {
        Require(
            service.Numbers["attach_cycle"] == service.Numbers["trigger_cycle"] &&
            service.Numbers["trigger_ordinal"] ==
                service.Numbers["attach_ordinal"] + 1,
            "Service observation did not attach immediately before Center-down.");
        Require(
            service.Numbers["detach_cycle"] == service.Numbers["exit_cycle"] &&
            service.Numbers["exit_ordinal"] ==
                service.Numbers["detach_ordinal"] + 1,
            "Service observation did not detach immediately before exit-down.");
        Require(
            service.Numbers["observation_callback_count"] > 0,
            "Service observation callback did not see the native test frame.");
        Require(
            control.Numbers["attach_cycle"] == service.Numbers["attach_cycle"] &&
            control.Numbers["detach_cycle"] == service.Numbers["detach_cycle"],
            "Control observation boundaries do not match the service run.");
    }

    void WriteArtifactSummary(
        ProbeScenarioResult service,
        ProbeScenarioResult? control)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        File.WriteAllLines(
            Path.Combine(ArtifactDirectory, "identities.tsv"),
            [
                "input\tpath_or_value\tsha256",
                $"firmware\t{FirmwarePath}\t{service.Text["firmware_sha256"]}",
                $"gdfs\t{GdfsPath}\t{service.Text["gdfs_sha256"]}",
                $"modem\t{ModemPath}\t{service.Text["modem_sha256"]}",
                $"sim\t{SimIdentity}\t--",
                "virtual_sim\ttrue\t--",
                "power_pressed_initially\ttrue\t--",
                $"power_release_cycle\t{MiaMachine.DefaultPowerKeyReleaseCycle}\t--",
                $"core_scheduling\t{MiaCoreSchedulingMode.CoarseParallel}\t--",
                $"boot_instruction_budget\t{BootInstructionBudget}\t--",
                $"idle_stability_cycles\t{IdleStabilityCycleBudget}\t--",
                $"key_down_cycles\t{KeyDownCycleBudget}\t--",
                $"trigger_key_down_cycles\t{TriggerKeyDownCycleBudget}\t--",
                $"key_post_cycles\t{KeyPostCycleBudget}\t--",
                $"menu_settle_cycles\t{MenuSettleCycleBudget}\t--",
                $"observation_start_cycle\t{ObservationStartCycle}\t--",
                $"observation_window_cycles\t{ObservationWindowCycleBudget}\t--",
                $"observation_instruction_safety_budget\t" +
                $"{ObservationInstructionSafetyBudget}\t--",
                $"observation_setup\t{service.Text["observation_setup"]}\t--",
            ]);
        File.WriteAllLines(
            Path.Combine(ArtifactDirectory, "native-screens.tsv"),
            [
                "screen\tframebuffer_sha256",
                $"stable-idle\t{StandbyHash}",
                $"service-menu\t{ServiceMenuHash}",
                $"service-tests\t{ServiceTestsHash}",
                $"led-illumination-selected\t{LedSelectedHash}",
                $"led-illumination-test\t{LedTestHash}",
            ]);
        var windows = new List<string>
        {
            "scenario\tselected_hash\tstart_cycle\tend_cycle\tduration_cycles\t" +
            "center_triggers\tin_window_keys\tcallback_count\t" +
            "callback_first_cycle\tcallback_last_cycle\twindow_end_hash",
            WindowLine(service),
        };
        if (control is { } matchedControl)
        {
            windows.Add(WindowLine(matchedControl));
        }
        File.WriteAllLines(
            Path.Combine(ArtifactDirectory, "matched-windows.tsv"),
            windows);
        File.WriteAllText(
            Path.Combine(ArtifactDirectory, "README.md"),
            "# Native LED/Illumination service-test evidence\n\n" +
            "The probe boots unmodified firmware with the compact GDFS image and " +
            "virtual SIM. It waits for the stable native idle framebuffer, enters " +
            "`>*<<*<*` through physical keypad contacts, and uses Down/Center keys " +
            "to select `LED/Illumination`. No framebuffer overlay or firmware-state " +
            "write is used.\n\n" +
            "The service timeline attaches its aggregate display observation at " +
            $"machine cycle {ObservationStartCycle.ToString(CultureInfo.InvariantCulture)} " +
            "and sends Center-down as the immediately following action at the same " +
            "cycle. It detaches at cycle " +
            $"{(ObservationStartCycle + ObservationWindowCycleBudget).ToString(CultureInfo.InvariantCulture)} " +
            "and sends exit-down as the immediately following action at that same " +
            "cycle. The retained `window-end.rgb332` snapshot is captured before " +
            "exit. The matched control follows the same native route, attaches at " +
            "the same cycle, and holds the selected row for the same cycle window " +
            "without Center.\n\n" +
            "Reproduce with `dotnet run tools/probe_led_service_menu.cs -- " +
            "--verify-boundaries`. Raw RGB332 frames, lossless PPM renderings, " +
            "machine-cycle action timelines, input identities, and matched-window " +
            "metrics are in this directory.\n");
    }

    static string WindowLine(
        ProbeScenarioResult run) =>
        $"{run.Text["scenario"]}\t{run.Text["selected_hash"]}\t" +
        $"{run.Numbers["window_start_cycle"]}\t{run.Numbers["window_end_cycle"]}\t" +
        $"{run.Numbers["window_end_cycle"] - run.Numbers["window_start_cycle"]}\t" +
        $"{run.Numbers["center_trigger_count"]}\t" +
        $"{run.Numbers["window_key_event_count"]}\t" +
        $"{run.Numbers["observation_callback_count"]}\t" +
        $"{run.Numbers["observation_first_cycle"]}\t" +
        $"{run.Numbers["observation_last_cycle"]}\t" +
        run.Text["window_end_hash"];

    static void WriteTimeline(string? directory, List<string> timeline)
    {
        if (directory is null)
        {
            return;
        }
        File.WriteAllLines(
            Path.Combine(directory, "timeline.tsv"),
            [
                "ordinal\tcycle\tscenario\taction\tdetail\tframe_version\t" +
                "framebuffer_sha256",
                .. timeline,
            ]);
    }

    static void SaveFrame(
        string? directory,
        string fileName,
        ReadOnlySpan<byte> framebuffer)
    {
        if (directory is null)
        {
            return;
        }
        Require(
            framebuffer.Length == S4595Display.Width * S4595Display.Height,
            "Framebuffer snapshot has the wrong size.");
        string stem = Path.Combine(directory, fileName);
        File.WriteAllBytes(stem + ".rgb332", framebuffer.ToArray());
        using var output = File.Create(stem + ".ppm");
        using (var header = new StreamWriter(output, leaveOpen: true))
        {
            header.Write($"P6\n{S4595Display.Width} {S4595Display.Height}\n255\n");
        }
        Span<byte> rgb = stackalloc byte[3];
        foreach (byte pixel in framebuffer)
        {
            var expanded = S4595Display.ExpandRgb332(pixel);
            rgb[0] = expanded.Red;
            rgb[1] = expanded.Green;
            rgb[2] = expanded.Blue;
            output.Write(rgb);
        }
    }

    static void RunUntilAtLeast(MiaMachine machine, long cycle)
    {
        while (!machine.IsStopped && machine.Cycles < cycle)
        {
            RunSome(machine);
        }
        Require(
            !machine.IsStopped,
            $"Machine stopped at cycle {machine.Cycles}: {machine.StopReason}");
    }

    static void RunSome(MiaMachine machine)
    {
        machine.RunWorkItems(65_536);
        Require(
            !machine.IsStopped,
            $"Machine stopped at cycle {machine.Cycles}: {machine.StopReason}");
    }

    static string FrameHash(MiaMachine machine) => Hash(machine.Frame.Span);

    static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    static string FormatTraceBus(AccessTraceBus bus) => bus switch
    {
        AccessTraceBus.Avr => "avr",
        AccessTraceBus.PrimaryI2c => "primary-i2c",
        AccessTraceBus.ArmMmio => "arm-mmio",
        _ => throw new ArgumentOutOfRangeException(nameof(bus)),
    };

    static string FormatTraceOperation(AccessTraceOperation operation) =>
        operation switch
        {
            AccessTraceOperation.Read => "read",
            AccessTraceOperation.Write => "write",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    static void WriteLedServiceDisassemblyTrace(
        string path,
        string firmwareSha256,
        long startCycle,
        long endCycle,
        IReadOnlyList<LedServiceSignalEvent> sends,
        IReadOnlyList<LedServiceSignalEvent> receives,
        IReadOnlyList<LedServiceSignalEvent> ledCommandSends,
        IReadOnlyList<LedServiceSignalEvent> ledCommandReceives,
        IReadOnlyList<LedServiceI2cTransaction> transactions,
        IReadOnlyList<LedServiceExecutionEvent> serviceEvents,
        IReadOnlyList<LedServiceAvrWrite> avrWrites,
        long avrWriteDroppedCount)
    {
        using FileStream output = File.Create(path);
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", "mia-led-service-disassembly-trace-v2");
        writer.WriteString("firmwareSha256", firmwareSha256);
        writer.WriteNumber("startCycle", startCycle);
        writer.WriteNumber("endCycleExclusive", endCycle);
        writer.WriteNumber("durationCycles", endCycle - startCycle);
        writer.WriteString("i2cProcess", "0x21");
        writer.WriteString("i2cRequestSignal", "0x07ac");
        WriteLedServiceSignalEvents(writer, "i2cRequestSends", sends);
        WriteLedServiceSignalEvents(writer, "i2cRequestReceives", receives);
        writer.WriteString("ledCommandSignal", "0x0bd7");
        WriteLedServiceSignalEvents(writer, "ledCommandSends", ledCommandSends);
        WriteLedServiceSignalEvents(
            writer,
            "ledCommandReceives",
            ledCommandReceives);
        writer.WriteStartArray("primaryI2cTransactions");
        foreach (LedServiceI2cTransaction transaction in transactions)
        {
            writer.WriteStartObject();
            writer.WriteNumber("cycle", transaction.Cycle);
            writer.WriteNumber("instruction", transaction.Instruction);
            writer.WriteString("pc", $"0x{transaction.Pc:x6}");
            writer.WriteString("process", $"0x{transaction.Process:x2}");
            writer.WriteString("address", $"0x{transaction.Address:x2}");
            writer.WriteString("payload", transaction.Payload);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("serviceEvents");
        foreach (LedServiceExecutionEvent item in serviceEvents)
        {
            writer.WriteStartObject();
            writer.WriteNumber("cycle", item.Cycle);
            writer.WriteString("pc", $"0x{item.Pc:x6}");
            writer.WriteString("process", $"0x{item.Process:x2}");
            writer.WriteString("stateBytes", item.StateBytes);
            writer.WriteString("portBytes", item.PortBytes);
            writer.WriteString("registers", item.Registers);
            writer.WriteString("stackBytes", item.StackBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("avrWriteDroppedCount", avrWriteDroppedCount);
        writer.WriteStartArray("avrWrites");
        foreach (LedServiceAvrWrite write in avrWrites)
        {
            writer.WriteStartObject();
            writer.WriteNumber("asicCycle", write.AsicCycle);
            writer.WriteNumber("avrCycle", write.AvrCycle);
            writer.WriteString("pc", $"0x{write.Pc:x6}");
            writer.WriteString("stackPointer", $"0x{write.StackPointer:x6}");
            writer.WriteString("process", $"0x{write.Process:x2}");
            writer.WriteString("address", $"0x{write.Address:x6}");
            writer.WriteString("oldValue", $"0x{write.OldValue:x2}");
            writer.WriteString("newValue", $"0x{write.NewValue:x2}");
            writer.WriteString("mask", $"0x{write.Mask:x2}");
            writer.WriteString(
                "effectiveValue",
                $"0x{write.EffectiveValue:x2}");
            writer.WriteBoolean("hookConsumed", write.HookConsumed);
            writer.WriteString("registers", write.Registers);
            writer.WriteString("stackBytes", write.StackBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    static void WriteLedServiceSignalEvents(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<LedServiceSignalEvent> events)
    {
        writer.WriteStartArray(propertyName);
        foreach (LedServiceSignalEvent item in events)
        {
            writer.WriteStartObject();
            writer.WriteNumber("cycle", item.Cycle);
            writer.WriteNumber("instruction", item.Instruction);
            writer.WriteString("process", $"0x{item.Process:x2}");
            writer.WriteString("peerProcess", $"0x{item.PeerProcess:x2}");
            writer.WriteString("returnPc", $"0x{item.ReturnPc:x6}");
            writer.WriteString(
                "logicalSignalAddress",
                $"0x{item.LogicalSignalAddress:x6}");
            writer.WriteString(
                "physicalSignalAddress",
                $"0x{item.PhysicalSignalAddress:x6}");
            writer.WriteString("signal", $"0x{item.Signal:x4}");
            writer.WriteString("signalBytes", item.SignalBytes);
            writer.WriteString(
                "logicalBufferAddress",
                $"0x{item.LogicalBufferAddress:x6}");
            writer.WriteString(
                "physicalBufferAddress",
                $"0x{item.PhysicalBufferAddress:x6}");
            writer.WriteString("bufferBytes", item.BufferBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static bool IsLedServiceTracePc(int pc) => pc is
        0x078b74 or 0x078b90 or 0x078bac or 0x078bae or
        0x078bb0 or 0x078bcc or
        0x07899b or 0x0789a7 or 0x0789b3 or 0x0789bf or
        0x0789cb or 0x0789e1 or
        0x08f8f4 or 0x08f912 or 0x08f914 or 0x08f921 or
        0x08f924 or 0x08f926 or
        0x08f930 or 0x08f944 or 0x08f951 or 0x08f954 or
        0x08f95e or 0x08f960 or 0x08f96d or 0x08f970 or
        0x08f980 or 0x08f982 or
        0x08fa10 or 0x08fa1c or 0x08fa24 or 0x08fa76 or
        0x08fa88 or
        0x0761c9 or 0x07620d or 0x076238 or 0x076267 or
        0x0762b8 or 0x0762e8 or 0x076318 or 0x07643d;

    static bool TryCaptureLedServiceSignal(
        MiaMachine machine,
        byte process,
        byte peerProcess,
        int returnPc,
        out LedServiceSignalEvent result)
    {
        const int MaximumSignalBytes = 32;
        const int MaximumBufferBytes = 96;
        var cpu = machine.Cpu;
        int logicalSignal =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        if ((logicalSignal & 0xffff) == 0xfdfd)
        {
            result = default;
            return false;
        }
        int physicalSignal = cpu.TranslateDataAddress(logicalSignal);
        if ((uint)physicalSignal > (uint)(cpu.Data.Length - 8))
        {
            result = default;
            return false;
        }

        ushort signal = (ushort)(
            cpu.ReadData(physicalSignal) |
            cpu.ReadData(physicalSignal + 1) << 8);
        int logicalBuffer =
            cpu.ReadData(physicalSignal + 4) |
            cpu.ReadData(physicalSignal + 5) << 8 |
            cpu.ReadData(physicalSignal + 6) << 16;
        int physicalBuffer = cpu.TranslateDataAddress(logicalBuffer);
        string bufferBytes = (uint)physicalBuffer < (uint)cpu.Data.Length
            ? Convert.ToHexString(cpu.Data.AsSpan(
                physicalBuffer,
                Math.Min(MaximumBufferBytes, cpu.Data.Length - physicalBuffer)))
            : string.Empty;
        result = (
            machine.Cycles,
            machine.ExecutedInstructions,
            process,
            peerProcess,
            returnPc,
            logicalSignal,
            physicalSignal,
            signal,
            Convert.ToHexString(cpu.Data.AsSpan(
                physicalSignal,
                Math.Min(MaximumSignalBytes, cpu.Data.Length - physicalSignal))),
            logicalBuffer,
            physicalBuffer,
            bufferBytes);
        return true;
    }

    static byte ReadLedSignalSource(Cpu cpu)
    {
        int logicalSignal =
            cpu.Data[16] | cpu.Data[17] << 8 | cpu.Data[18] << 16;
        int physicalSignal = cpu.TranslateDataAddress(logicalSignal);
        return physicalSignal > 0 && physicalSignal < cpu.Data.Length
            ? cpu.ReadData(physicalSignal - 1)
            : (byte)0xff;
    }

    static int ReadAvrReturnPc(Cpu cpu)
    {
        int stack = cpu.SP;
        return stack <= cpu.Data.Length - 4
            ? cpu.ReadData(stack + 1) << 16 |
              cpu.ReadData(stack + 2) << 8 |
              cpu.ReadData(stack + 3)
            : 0;
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine($"SERVICE_PROBE_FAIL {error.Message}");
    return 1;
}
