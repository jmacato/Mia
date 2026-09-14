// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Mia.Emulator.Runtime;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaMachineTests
{
    [Fact]
    public void ExternalPowerProfileReachesThePrimaryI2cCompanion()
    {
        using var machine = new MiaMachine(
            [0x00, 0x00],
            virtualSim: false,
            externalPowerConnectedInitially: true);

        machine.PowerPorts.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.PortStatusCommand]);

        Assert.True(machine.PowerPorts.ExternalPowerConnected);
        Assert.Equal(
            MiaPowerPortController.ExternalPowerLevel,
            machine.PowerPorts.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void PowerPortSiliconRevisionIsSelectableAtTheMachineBoundary()
    {
        using var machine = new MiaMachine(
            [0x00, 0x00],
            virtualSim: false,
            powerPortSiliconRevision: 0xf4);

        machine.PowerPorts.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.RevisionCommand]);

        Assert.Equal(
            0xf4,
            machine.PowerPorts.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public async Task BluetoothPeerConfigurationRunsThroughTheArmOwner()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            modemBih: BuildLoopingModem(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        var statusChanged = new TaskCompletionSource();
        MiaBluetoothEmulationStatus publishedStatus = default;
        machine.BluetoothEmulationStatusChanged += status =>
        {
            publishedStatus = status;
            statusChanged.TrySetResult();
        };

        machine.ConfigureEmulatedBluetoothPeer(
            enabled: false,
            pin: "8675309",
            incomingObjectPushEnabled: false);

        var status = machine.GetBluetoothEmulationStatus();
        Assert.False(status.Enabled);
        Assert.Equal("Mia Peer", status.PeerName);
        Assert.Equal("11:22:33:44:55:66", status.PeerAddress);
        Assert.Equal("8675309", status.Pin);
        Assert.False(status.IncomingObjectPushEnabled);
        Assert.Equal(0, status.PairingCompletedCount);
        await statusChanged.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        Assert.Equal(status, publishedStatus);
    }

    [Fact]
    public void BluetoothPeerConfigurationRejectsAnInvalidPin()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            modemBih: BuildLoopingModem(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);

        Assert.Throws<ArgumentException>(() =>
            machine.ConfigureEmulatedBluetoothPeer(
                enabled: true,
                pin: "12ab",
                incomingObjectPushEnabled: true));
    }

    [Fact]
    public async Task GuestCycleDeadlineCompletesWithoutHostPolling()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        long targetCycle = machine.Cycles + 64;

        Task wait = machine.WaitUntilCycleAsync(targetCycle).AsTask();
        Assert.False(wait.IsCompleted);

        machine.RunWorkItems(128);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(machine.Cycles >= targetCycle);
    }

    [Fact]
    public void LiveGsmIsOwnedAndExposedByTheCoreMachine()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            liveGsm: new MiaLiveGsmOptions());

        var liveGsm = Assert.IsType<MiaLiveGsmController>(machine.LiveGsm);
        Assert.True(liveGsm.TryQueueIncomingSms("5551234", "hello", out _));
        Assert.True(liveGsm.IncomingTransactionActive);
        var status = liveGsm.GetStatus(machine);
        Assert.True(status.IncomingTransactionActive);
        Assert.Equal(1, status.QueuedIncomingSms);
    }

    [Fact]
    public void LiveGsmTruncatesSmsThatExceedsTheProvenFirmwareEnvelope()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            liveGsm: new MiaLiveGsmOptions());

        var liveGsm = Assert.IsType<MiaLiveGsmController>(machine.LiveGsm);
        Assert.True(liveGsm.TryQueueIncomingSms(
            "5551234",
            "12345678901",
            out var result));

        Assert.Contains("truncated to 10 characters", result, StringComparison.Ordinal);
        Assert.Equal(1, liveGsm.GetStatus(machine).QueuedIncomingSms);
    }

    [Fact]
    public void LiveGsmDefersRequestsSubmittedDuringAnActiveTransaction()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            liveGsm: new MiaLiveGsmOptions());

        var liveGsm = Assert.IsType<MiaLiveGsmController>(machine.LiveGsm);
        Assert.True(liveGsm.TryQueueIncomingCall("5551000", false, out _));
        Assert.True(liveGsm.TryQueueIncomingSms(
            "5552000",
            "after call",
            out var result));

        Assert.Contains("behind the active GSM transaction at position 1", result, StringComparison.Ordinal);
        Assert.Equal(1, liveGsm.DeferredIncomingTransactionCount);
        var status = liveGsm.GetStatus(machine);
        Assert.Equal(1, status.QueuedIncomingCalls);
        Assert.Equal(0, status.QueuedIncomingSms);
        Assert.Equal(1, status.DeferredIncomingTransactions);
    }

    [Fact]
    public void LiveGsmBoundsTheDeferredIncomingQueue()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            liveGsm: new MiaLiveGsmOptions());

        var liveGsm = Assert.IsType<MiaLiveGsmController>(machine.LiveGsm);
        Assert.True(liveGsm.TryQueueIncomingCall("5551000", false, out _));
        for (var index = 0; index < 8; index++)
        {
            Assert.True(liveGsm.TryQueueIncomingSms(
                $"55520{index:00}",
                $"queued {index}",
                out _));
        }

        Assert.False(liveGsm.TryQueueIncomingSms(
            "5552999",
            "overflow",
            out var result));
        Assert.Contains("incoming GSM queue is full", result, StringComparison.Ordinal);
        Assert.Equal(8, liveGsm.DeferredIncomingTransactionCount);
    }

    [Fact]
    public void MobileDisconnectReleasesCellWhenRrLeavesTrafficBeforeMph()
    {
        Assert.True(MiaLiveGsmController.HasNativeCallExitBoundary(
            setupCount: 1,
            setupBaseline: 0,
            disconnectCount: 1,
            disconnectBaseline: 0,
            nativeRrState: 1,
            nativeMphState: 8));
        Assert.False(MiaLiveGsmController.HasNativeCallExitBoundary(
            setupCount: 1,
            setupBaseline: 0,
            disconnectCount: 0,
            disconnectBaseline: 0,
            nativeRrState: 1,
            nativeMphState: 8));
    }

    [Fact]
    public void LiveGsmRejectsAmbiguousSeparateRadioSources()
    {
        Assert.Throws<ArgumentException>(() => new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            rfSignalSource: AsicNoSignalRfSource.Instance,
            liveGsm: new MiaLiveGsmOptions()));
    }

    [Fact]
    public void LiveGsmKeepsConfiguredCarrierPowerConstantAcrossActions()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            liveGsm: new MiaLiveGsmOptions());
        var liveGsm = Assert.IsType<MiaLiveGsmController>(machine.LiveGsm);
        var transaction = new AsicRfTransaction(
            0,
            Convert.FromHexString("00300046000000907790770100F907"));
        var trafficAction = new AsicTimeGeneratorActionExecution(
            ProgramSelector: 0,
            SchedulePortAddress: 0x0881,
            ActionId: 0x24,
            Operand: 0x68,
            QuarterBit: 4827,
            OccurrenceIndex: 0,
            OccurrenceCount: 1);
        var idleRssiAction = new AsicTimeGeneratorActionExecution(
            ProgramSelector: 0,
            SchedulePortAddress: 0x0881,
            ActionId: 0x08,
            Operand: 0x01da,
            QuarterBit: 4475,
            OccurrenceIndex: 0,
            OccurrenceCount: 1);

        Assert.Equal(0x0800, liveGsm.ReadRawSample(4, transaction, trafficAction));
        Assert.Equal(0x7f80, liveGsm.ReadRawSample(1, transaction, trafficAction));
        Assert.Equal(0x7f80, liveGsm.ReadRawSample(2, transaction, trafficAction));
        Assert.Equal(0x7f80, liveGsm.ReadRawSample(1, transaction, idleRssiAction));
        Assert.Equal(1, liveGsm.RssiSampleCount);
        Assert.Equal(0x7f80, liveGsm.LastRssiRawSample);

        // The native level and the transient UI allocation are deliberately
        // separate status fields; the latter is not the antenna level.
        machine.Cpu.Data[0x027e98] = 5;
        machine.Cpu.Data[0x050817] = 0x7f;
        var status = liveGsm.GetStatus(machine);
        Assert.Equal(5, status.RssiLevel);
        Assert.Equal(0x7f, status.RssiWidgetByte);

        liveGsm.CarrierRawSample = 0x3456;

        Assert.Equal(0x3456, liveGsm.ReadRawSample(1, transaction, trafficAction));
        Assert.Equal(0x3456, liveGsm.ReadRawSample(2, transaction, trafficAction));
        Assert.Equal(0x3456, liveGsm.CarrierRawSample);
    }

    [Fact]
    public void GdfsCaptureDrainsPendingNorWritesAndReturnsAStableCopy()
    {
        byte[] gdfs = Enumerable.Repeat((byte)0xff, GdfsImage.RawLength).ToArray();
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            gdfs,
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue);
        const int logicalAddress = AsicFlashMemory.LogicalGdfsBase + 0x42;

        WriteWord(machine.Cpu, logicalAddress, 0x0050);
        WriteWord(machine.Cpu, logicalAddress, 0x0040);
        WriteWord(machine.Cpu, logicalAddress, 0x1234);

        var capture = machine.CaptureGdfsImage();
        Assert.Equal(0x34, capture[0x42]);
        Assert.Equal(0x12, capture[0x43]);

        machine.Cpu.ProgBytes[GdfsImage.RawAddress + 0x42] = 0x56;
        Assert.Equal(0x34, capture[0x42]);
    }

    [Fact]
    public void StartsWithThePhysicalNoPowerButtonAtBothHardwareBoundaries()
    {
        using var machine = new MiaMachine([0x00, 0x00], virtualSim: false);

        Assert.True(machine.PowerPorts.PowerPressed);
        machine.Cpu.WriteData(
            AsicKeypad.ScanControlAddress,
            AsicKeypad.NoPowerScanMask);
        Assert.Equal(
            (byte)(AsicKeypad.IdleRows & ~AsicKeypad.NoPowerRowMask),
            machine.Cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialPowerHoldInterruptsWhenFirmwareEnablesKeyDetection(bool dedicatedWorkers)
    {
        using var machine = new MiaMachine([0x00, 0x00],
            virtualSim: false, dedicatedWorkers: dedicatedWorkers);
        long raised = machine.InterruptController.GetRaisedCount(AsicInterruptController.KeypadSource);

        machine.Cpu.WriteData(AsicKeypad.ScanControlAddress,
            AsicKeypad.InterruptEnable | AsicKeypad.NoPowerScanMask);

        Assert.Equal(raised + 1,
            machine.InterruptController.GetRaisedCount(AsicInterruptController.KeypadSource));
    }

    [Fact]
    public void StopsClosedWhenExecutionReachesErasedFlash()
    {
        using var machine = new MiaMachine([0x00, 0x00], virtualSim: false);

        Assert.Equal(1, machine.RunWorkItems(1));
        Assert.Equal(1, machine.ExecutedInstructions);
        Assert.Equal(1, machine.RunWorkItems(1));

        Assert.True(machine.IsStopped);
        Assert.Contains("entered erased flash", machine.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineClockTracksTheThirteenToTwelveAvrRatioAfterWork()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue);

        Assert.Equal(1_000, machine.RunWorkItems(1_000));

        Assert.Equal(
            machine.Cpu.Cycles * MiaSystemClock.AsicCyclesPerSecond /
                MiaSystemClock.AvrCyclesPerSecond,
            machine.Cycles);
        Assert.Equal(machine.Clock.Cycles, machine.Cycles);
    }

    [Fact]
    public void NativeMachineExecutesAvrWorkOnItsDedicatedThread()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue);
        var caller = Environment.CurrentManagedThreadId;

        Assert.Equal(10, machine.RunWorkItems(10));

        var avrWorker = Assert.Single(
            machine.Workers,
            worker => worker.Name == "AVR core");
        Assert.NotEqual(caller, avrWorker.ThreadId);
        Assert.True(avrWorker.CompletedWorkCount >= 1);
    }

    [Fact]
    public void EveryMachineWorkerOwnsItsOwnDedicatedThread()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            modemBih: BuildLoopingModem(),
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue);

        Assert.NotEmpty(machine.Workers);
        Assert.All(machine.Workers, worker =>
        {
            Assert.True(worker.HasDedicatedThread, worker.Name);
            Assert.NotEqual(0, worker.ThreadId);
        });
        Assert.Equal(
            machine.Workers.Count,
            machine.Workers.Select(worker => worker.ThreadId).Distinct().Count());
    }

    [Fact]
    public void BrowserStyleMachineRunsAllLogicalTargetsInline()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            modemBih: BuildLoopingModem(),
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CycleLocked,
            dedicatedWorkers: false);

        Assert.NotEmpty(machine.Workers);
        Assert.All(machine.Workers, worker =>
        {
            Assert.False(worker.HasDedicatedThread, worker.Name);
            Assert.Equal(0, worker.ThreadId);
        });

        Assert.Equal(10_000, machine.RunWorkItems(10_000));
        Assert.True(machine.Modem!.Instructions > 0);
    }

    [Fact]
    public void IdlePeripheralWorkersRemainAsleepDuringCoreOnlyExecution()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        var peripheralWorkers = machine.Workers
            .Where(worker => worker.Name != "AVR core")
            .ToArray();
        var initialWakeCounts = peripheralWorkers
            .ToDictionary(worker => worker, worker => worker.WakeCount);

        Assert.Equal(100_000, machine.RunWorkItems(100_000));

        Assert.All(
            peripheralWorkers,
            worker => Assert.Equal(
                initialWakeCounts[worker],
                worker.WakeCount));
    }

    [Fact]
    public void InlineCoreSynchronizationDoesNotAllocatePerInstruction()
    {
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            modemBih: BuildLoopingModem(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            dedicatedWorkers: false);
        machine.RunWorkItems(1_000);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        int completed = machine.RunWorkItems(100_000);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(100_000, completed);
        Assert.False(machine.IsStopped, machine.StopReason);
        Assert.True(machine.Modem!.Instructions > 0);
        Assert.True(allocated < 32_768, $"Core synchronization allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoarseParallelModeExecutesBothUnmodifiedInstructionStreams(bool dedicatedWorkers)
    {
        byte[] modem = BuildLoopingModem();
        using var machine = new MiaMachine(
            [0xff, 0xcf], // RJMP .
            modemBih: modem,
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            dedicatedWorkers: dedicatedWorkers);

        Assert.Equal(100_000, machine.RunWorkItems(100_000));

        Assert.False(machine.IsStopped, machine.StopReason);
        Assert.Equal(100_000, machine.ExecutedInstructions);
        Assert.True(machine.Modem!.Instructions > 0);
        Assert.True(machine.Modem.Cycles >= machine.Cpu.Cycles);
        var avrWorker = Assert.Single(
            machine.Workers,
            worker => worker.Name == "AVR core");
        Assert.NotNull(machine.ArmWorkerThreadId);
        Assert.Equal(dedicatedWorkers, avrWorker.ThreadId != machine.ArmWorkerThreadId);
    }

    [Fact]
    public void StableFirmwareIdleLoopFastForwardMatchesNativeExecution()
    {
        byte[] firmware = BuildIdleLoopFirmware();
        using var fast = new MiaMachine(
            firmware,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        using var native = new MiaMachine(
            firmware,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue)
        {
            IdleFastForwardEnabled = false,
        };
        ConfigureIdleLoopState(fast);
        ConfigureIdleLoopState(native);
        var fastEvents = 0;
        var nativeEvents = 0;
        fast.Clock.Schedule(() => fastEvents++, 1_000);
        native.Clock.Schedule(() => nativeEvents++, 1_000);

        Assert.Equal(1, fast.RunWorkItems(1_000));
        Assert.Equal(1, fast.IdleFastForwardCount);
        Assert.True(fast.IdleFastForwardedInstructions > 0);
        Assert.Equal(
            fast.IdleFastForwardedInstructions,
            native.RunWorkItems((int)fast.IdleFastForwardedInstructions));

        Assert.Equal(native.ExecutedInstructions, fast.ExecutedInstructions);
        Assert.Equal(native.Cpu.Cycles, fast.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, fast.Clock.Cycles);
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
        Assert.Equal(native.Cpu.Data.AsSpan(0, 0x100).ToArray(),
            fast.Cpu.Data.AsSpan(0, 0x100).ToArray());
        Assert.Equal(native.Cpu.Data[0x0ac0], fast.Cpu.Data[0x0ac0]);
        Assert.Equal(native.Cpu.Data[0x00f603], fast.Cpu.Data[0x00f603]);
        Assert.Equal(native.Cpu.Data[0x02df0c], fast.Cpu.Data[0x02df0c]);
        Assert.Equal(0, fastEvents);
        Assert.Equal(0, nativeEvents);

        fast.IdleFastForwardEnabled = false;
        Assert.Equal(20, fast.RunWorkItems(20));
        Assert.Equal(20, native.RunWorkItems(20));
        Assert.Equal(1, fastEvents);
        Assert.Equal(1, nativeEvents);
        Assert.Equal(native.Cpu.Cycles, fast.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, fast.Clock.Cycles);
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
    }

    [Fact]
    public void IdleFastForwardResolvesTheUniqueFirmwareLoopLocation()
    {
        const int relocatedStartWord = 0x002345;
        using var machine = new MiaMachine(
            BuildIdleLoopFirmware(relocatedStartWord),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        ConfigureIdleLoopState(machine, relocatedStartWord);
        machine.Clock.Schedule(() => { }, 1_000);

        Assert.Equal(relocatedStartWord, machine.IdleFastForwardLoopStartWord);
        Assert.Equal(1, machine.RunWorkItems(1_000));
        Assert.Equal(1, machine.IdleFastForwardCount);
    }

    [Fact]
    public void HotspotProfilingPreservesVerifiedIdleAcceleration()
    {
        using var machine = new MiaMachine(
            BuildIdleLoopFirmware(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        ConfigureIdleLoopState(machine);
        machine.Diagnostics.StartExecutionHotspotProfiling();
        machine.Clock.Schedule(() => { }, 1_000);

        Assert.Equal(1, machine.RunWorkItems(1_000));
        Assert.Equal(1, machine.IdleFastForwardCount);

        var hotspot = Assert.Single(
            machine.Diagnostics.SnapshotExecutionHotspots(),
            candidate => candidate.ProgramCounter == 0x001587);
        Assert.True(hotspot.Executions > 0);
        Assert.True(hotspot.BackwardBranches > 0);
    }

    [Fact]
    public void IdleFastForwardDiagnosticsExplainARejectedLoopEntry()
    {
        using var machine = new MiaMachine(
            BuildIdleLoopFirmware(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue)
        {
            IdleFastForwardEnabled = false,
        };
        ConfigureIdleLoopState(machine);
        machine.Diagnostics.EnableIdleFastForwardDiagnostics();

        Assert.Equal(1, machine.RunWorkItems(1));

        var diagnostics = machine.Diagnostics
            .SnapshotIdleFastForwardDiagnostics();
        Assert.Equal(1, diagnostics.CandidateCount);
        Assert.Equal(0, diagnostics.SuccessCount);
        Assert.Equal(
            MiaIdleFastForwardBlockReason.Disabled,
            diagnostics.LastBlockReason);
        Assert.Contains(
            diagnostics.BlockCounts,
            block => block is
            {
                Reason: MiaIdleFastForwardBlockReason.Disabled,
                Count: 1,
            });
    }

    [Fact]
    public void VerifiedGdfsSectorHeaderFastPathPreservesTheFirmwareExitState()
    {
        using var machine = new MiaMachine(
            BuildKnownGdfsSectorHeaderFirmware(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        machine.Cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
        machine.Cpu.Data[4] = 0;
        machine.Cpu.Data[6] = 0xc9;
        machine.Cpu.Data[7] = 0x04;
        machine.Cpu.Data[20] = 0;
        machine.Cpu.Data[21] = 4;
        machine.Cpu.Data[24] = 1;
        machine.Cpu.SetStatusRegister(0xa1);
        machine.Clock.Schedule(() => { }, 1_000);

        Assert.Equal(1, machine.RunWorkItems(1));
        Assert.Equal(
            AsicFirmwareFastPaths.GdfsSectorHeaderMatchWord,
            machine.Cpu.PC);
        Assert.Equal(65, machine.Cpu.Cycles);
        Assert.Equal(57, machine.ExecutedInstructions);
        Assert.Equal(1, machine.GdfsSectorHeaderFastForwardCount);
        Assert.Equal(57, machine.GdfsSectorHeaderFastForwardedInstructions);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void CoarseGdfsScanMatchesBothCoresAndStopsBeforeTheDeviceDeadline(
        bool dedicatedWorkers, bool unitState)
    {
        byte[] firmware = BuildKnownGdfsSectorHeaderFirmware();
        if (unitState)
        {
            var words = MiaFirmwareRuntimeHooks.GdfsUnitStateLoopWords;
            for (int i = 0; i < words.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(firmware.AsSpan(
                    (AsicFirmwareFastPaths.GdfsUnitStateScanWord + i) * 2), words[i]);
            firmware.AsSpan(0x46c585, 2).Clear();
            firmware[0x46c584] = 0x80;
        }
        byte[] modem = BuildLoopingModem();
        using var fast = new MiaMachine(firmware, modemBih: modem,
            virtualSim: false, powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            dedicatedWorkers: dedicatedWorkers);
        using var native = new MiaMachine(firmware, modemBih: modem,
            virtualSim: false, powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            dedicatedWorkers: dedicatedWorkers)
        {
            GdfsSectorHeaderFastPathEnabled = false,
            GdfsUnitStateFastPathEnabled = false,
        };
        foreach (var machine in new[] { fast, native })
        {
            machine.Cpu.PC = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
            machine.Cpu.Data[6] = 0xc9;
            machine.Cpu.Data[7] = 0x04;
            machine.Cpu.Data[21] = 4;
            machine.Cpu.Data[24] = 1;
            if (unitState)
            {
                machine.Cpu.PC = AsicFirmwareFastPaths.GdfsUnitStateScanWord;
                machine.Cpu.Data[19] = 0xc6;
                machine.Cpu.SetUint16(30, 0xc586);
            }
            machine.Cpu.SetStatusRegister(0xa1);
            machine.Clock.Schedule(() => Assert.Fail("The scan crossed its device deadline"), 50);
        }

        Assert.Equal(1, fast.RunWorkItems(1));
        int instructions = unitState ? 16 : 31;
        Assert.Equal(instructions, fast.ExecutedInstructions);
        Assert.Equal(instructions, native.RunWorkItems(instructions));
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
        Assert.Equal(native.Cpu.Cycles, fast.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, fast.Clock.Cycles);
        Assert.Equal(native.ExecutedInstructions, fast.ExecutedInstructions);
        Assert.Equal(native.Cpu.Data.AsSpan(0, 0x60).ToArray(),
            fast.Cpu.Data.AsSpan(0, 0x60).ToArray());
        Assert.Equal(native.Modem!.Cycles, fast.Modem!.Cycles);
        Assert.Equal(native.Modem.Instructions, fast.Modem.Instructions);
        Assert.Equal(native.Modem.CurrentInstructionAddress, fast.Modem.CurrentInstructionAddress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoarseIdleFastForwardStillExecutesAnActiveModemToTheBarrier(bool dedicatedWorkers)
    {
        byte[] firmware = BuildIdleLoopFirmware();
        byte[] modem = BuildLoopingModem();
        using var fast = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            dedicatedWorkers: dedicatedWorkers);
        using var native = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
            dedicatedWorkers: dedicatedWorkers)
        {
            IdleFastForwardEnabled = false,
        };
        ConfigureIdleLoopState(fast);
        ConfigureIdleLoopState(native);
        var fastEvents = 0;
        var nativeEvents = 0;
        fast.Clock.Schedule(() => fastEvents++, 1_000);
        native.Clock.Schedule(() => nativeEvents++, 1_000);

        Assert.Equal(1, fast.RunWorkItems(1));
        Assert.Equal(1, fast.IdleFastForwardCount);
        Assert.True(fast.IdleFastForwardedInstructions > 0);
        Assert.True(fast.Modem!.Instructions > 0);
        Assert.True(fast.Modem.Cycles >= fast.Clock.Cycles);
        Assert.Equal(
            fast.IdleFastForwardedInstructions,
            native.RunWorkItems((int)fast.IdleFastForwardedInstructions));

        Assert.Equal(native.ExecutedInstructions, fast.ExecutedInstructions);
        Assert.Equal(native.Cpu.Cycles, fast.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, fast.Clock.Cycles);
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
        Assert.Equal(native.Modem!.Instructions, fast.Modem.Instructions);
        Assert.Equal(native.Modem.Cycles, fast.Modem.Cycles);
        Assert.Equal(native.Modem.CurrentInstructionAddress,
            fast.Modem.CurrentInstructionAddress);
        Assert.Equal(0, fastEvents);
        Assert.Equal(0, nativeEvents);
    }

    [Fact]
    public void CoarseIdleFastForwardCompletesAPartialLoopBeforeCollapsingIt()
    {
        byte[] firmware = BuildIdleLoopFirmware();
        byte[] modem = BuildLoopingModem();
        using var fast = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel)
        {
            IdleFastForwardEnabled = false,
        };
        using var native = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel)
        {
            IdleFastForwardEnabled = false,
        };
        ConfigureIdleLoopState(fast);
        ConfigureIdleLoopState(native);

        Assert.Equal(7, fast.RunWorkItems(7));
        Assert.Equal(7, native.RunWorkItems(7));
        Assert.NotEqual(0x001587, fast.Cpu.PC);
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
        fast.IdleFastForwardEnabled = true;
        fast.Clock.Schedule(() => { }, 1_000);
        native.Clock.Schedule(() => { }, 1_000);
        long beforeCollapse = fast.ExecutedInstructions;

        int completed = fast.RunWorkItems(1_000);
        long collapsedInstructions = fast.ExecutedInstructions - beforeCollapse;

        Assert.InRange(completed, 2, 19);
        Assert.Equal(1, fast.IdleFastForwardCount);
        Assert.True(fast.IdleFastForwardedInstructions > 0);
        Assert.Equal(collapsedInstructions,
            native.RunWorkItems((int)collapsedInstructions));
        Assert.Equal(native.ExecutedInstructions, fast.ExecutedInstructions);
        Assert.Equal(native.Cpu.Cycles, fast.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, fast.Clock.Cycles);
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
        Assert.Equal(native.Cpu.Data.AsSpan(0, 0x100).ToArray(),
            fast.Cpu.Data.AsSpan(0, 0x100).ToArray());
        Assert.Equal(native.Cpu.Data[0x0ac0], fast.Cpu.Data[0x0ac0]);
        Assert.Equal(native.Modem!.Instructions, fast.Modem!.Instructions);
        Assert.Equal(native.Modem.Cycles, fast.Modem.Cycles);
        Assert.Equal(native.Modem.CurrentInstructionAddress,
            fast.Modem.CurrentInstructionAddress);
    }

    [Fact]
    public void CoarseIdleFastForwardStabilizesOneRealLoopBeforeCollapsing()
    {
        byte[] firmware = BuildIdleLoopFirmware();
        byte[] modem = BuildLoopingModem();
        using var fast = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
        using var native = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel)
        {
            IdleFastForwardEnabled = false,
        };
        ConfigureIdleLoopState(fast);
        ConfigureIdleLoopState(native);
        fast.Cpu.Data[16] = 0;
        native.Cpu.Data[16] = 0;
        fast.Clock.Schedule(() => { }, 1_000);
        native.Clock.Schedule(() => { }, 1_000);

        int completed = fast.RunWorkItems(1_000);

        Assert.Equal(20, completed);
        Assert.Equal(1, fast.IdleFastForwardCount);
        Assert.True(fast.IdleFastForwardedInstructions > 0);
        Assert.Equal(fast.ExecutedInstructions,
            native.RunWorkItems((int)fast.ExecutedInstructions));
        Assert.Equal(native.Cpu.Cycles, fast.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, fast.Clock.Cycles);
        Assert.Equal(native.Cpu.PC, fast.Cpu.PC);
        Assert.Equal(native.Cpu.Data.AsSpan(0, 0x100).ToArray(),
            fast.Cpu.Data.AsSpan(0, 0x100).ToArray());
        Assert.Equal(native.Cpu.Data[0x0ac0], fast.Cpu.Data[0x0ac0]);
        Assert.Equal(native.Modem!.Instructions, fast.Modem!.Instructions);
        Assert.Equal(native.Modem.Cycles, fast.Modem.Cycles);
    }

    [Fact]
    public void CoarseGrantEndsAsSoonAsFirmwareEntersTheVerifiedIdleLoop()
    {
        byte[] firmware = BuildIdleLoopFirmware();
        byte[] modem = BuildLoopingModem();
        using var machine = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
        ConfigureIdleLoopState(machine);
        machine.Cpu.PC = 0x001586; // NOP immediately before the loop.
        machine.Clock.Schedule(() => { }, 1_000);

        Assert.Equal(1, machine.RunWorkItems(1_000));
        Assert.Equal(0x001587, machine.Cpu.PC);
        Assert.Equal(1, machine.ExecutedInstructions);
        Assert.Equal(0, machine.IdleFastForwardCount);

        Assert.Equal(1, machine.RunWorkItems(1_000));
        Assert.Equal(1, machine.IdleFastForwardCount);
        Assert.True(machine.IdleFastForwardedInstructions > 0);
    }

    [Fact]
    public void InteractiveCoarseGrantKeepsOwnershipAcrossIdleBoundaries()
    {
        byte[] firmware = BuildIdleLoopFirmware();
        byte[] modem = BuildLoopingModem();
        using var machine = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
        ConfigureIdleLoopState(machine);
        machine.Cpu.PC = 0x001586; // NOP immediately before the loop.
        machine.Clock.Schedule(() => { }, 1_000);
        var avrWorker = Assert.Single(
            machine.Workers,
            worker => worker.Name == "AVR core");
        long completedBefore = avrWorker.CompletedWorkCount;

        int completed = machine.RunInteractiveWorkItems(1_000);

        Assert.InRange(completed, 2, 1_000);
        Assert.True(machine.IdleFastForwardCount >= 1);
        Assert.InRange(machine.Cycles, 1_000, 60_050);
        Assert.Equal(completedBefore + 1, avrWorker.CompletedWorkCount);
    }

    [Fact]
    public void DeferredAvrClockSynchronizationMatchesEveryInstructionClocking()
    {
        byte[] firmware = BuildRegisterLoopFirmware();
        byte[] modem = BuildLoopingModem();
        using var deferred = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
        using var native = new MiaMachine(
            firmware,
            modemBih: modem,
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel)
        {
            DeferredAvrClockSynchronizationEnabled = false,
        };
        (int Pc, long CpuCycles, byte Value)? deferredBoundary = null;
        (int Pc, long CpuCycles, byte Value)? nativeBoundary = null;
        deferred.Clock.Schedule(() => deferredBoundary = (
            deferred.Cpu.PC,
            deferred.Cpu.Cycles,
            deferred.Cpu.Data[16]), 1_000);
        native.Clock.Schedule(() => nativeBoundary = (
            native.Cpu.PC,
            native.Cpu.Cycles,
            native.Cpu.Data[16]), 1_000);

        Assert.Equal(10_000, deferred.RunWorkItems(10_000));
        Assert.Equal(10_000, native.RunWorkItems(10_000));

        Assert.NotNull(deferredBoundary);
        Assert.Equal(nativeBoundary, deferredBoundary);
        Assert.Equal(native.ExecutedInstructions, deferred.ExecutedInstructions);
        Assert.Equal(native.Cpu.Cycles, deferred.Cpu.Cycles);
        Assert.Equal(native.Clock.Cycles, deferred.Clock.Cycles);
        Assert.Equal(native.Cpu.PC, deferred.Cpu.PC);
        Assert.Equal(native.Cpu.Data.AsSpan(0, 0x100).ToArray(),
            deferred.Cpu.Data.AsSpan(0, 0x100).ToArray());
        Assert.Equal(native.Modem!.Instructions, deferred.Modem!.Instructions);
        Assert.Equal(native.Modem.Cycles, deferred.Modem.Cycles);
        Assert.Equal(native.Modem.CurrentInstructionAddress,
            deferred.Modem.CurrentInstructionAddress);
    }

    [Fact]
    public void IdleLoopFastForwardHonorsDeferredTickWork()
    {
        using var machine = new MiaMachine(
            BuildIdleLoopFirmware(),
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue);
        ConfigureIdleLoopState(machine);
        var callbackCount = 0;
        machine.Cpu.ScheduleAfterTick(() => callbackCount++);

        Assert.Equal(1, machine.RunWorkItems(1));

        Assert.Equal(0, machine.IdleFastForwardCount);
        Assert.Equal(1, machine.ExecutedInstructions);
        Assert.Equal(0x001588, machine.Cpu.PC);
        Assert.Equal(1, callbackCount);
    }

    static byte[] BuildLoopingModem()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xeafffffe); // B .
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0xeafffffe);
        byte[] bih = new byte[ArmModemImage.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bih, ArmModemFlash.BaseAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(bih.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bih, ArmModemImage.HeaderLength);
        return bih;
    }

    static byte[] BuildIdleLoopFirmware(int startWord = 0x001587)
    {
        ushort[] words =
        [
            0x94f8, 0xe000, 0xbf0b, 0x9100, 0xf603,
            0x3001, 0xf069, 0xe0e2, 0xbfeb, 0xedff,
            0xe0ec, 0x8100, 0x6200, 0xe0e0, 0xbfeb,
            0xe0fa, 0xece0, 0x8300, 0x9478, 0xcfec,
        ];
        byte[] firmware = new byte[(startWord + words.Length) * 2];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                firmware.AsSpan((startWord + index) * 2),
                words[index]);
        }
        return firmware;
    }

    static byte[] BuildKnownGdfsSectorHeaderFirmware()
    {
        var firmware = Enumerable.Repeat(
            (byte)0xff,
            0x59040c).ToArray();
        ushort[] loop =
        [
            0x2044, 0xf009, 0xc044, 0x3040, 0xe000, 0x0750,
            0xe001, 0x0760, 0xe000, 0x0770, 0xf5e0, 0x2f28,
            0x2711, 0x0f04, 0x1f15, 0x1f26, 0x5000, 0x4010,
            0x4228, 0x2fe0, 0x2ff1, 0xbf2b, 0x8001, 0x8012,
            0x1406, 0x0417, 0xf539,
        ];
        for (int index = 0; index < loop.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                firmware.AsSpan(
                    (AsicFirmwareFastPaths.GdfsSectorHeaderScanWord + index) *
                    sizeof(ushort)),
                loop[index]);
        }
        ushort[] tail = [0x5f47, 0x4f5f, 0x4f6f, 0x4f7f, 0xcfb9];
        for (int index = 0; index < tail.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                firmware.AsSpan((0x0cb4e6 + index) * sizeof(ushort)),
                tail[index]);
        }
        firmware[0x590401] = 0x34;
        firmware[0x590402] = 0x12;
        firmware[0x59040a] = 0xc9;
        firmware[0x59040b] = 0x04;
        return firmware;
    }

    static byte[] BuildRegisterLoopFirmware()
    {
        ushort[] words =
        [
            0xe000, // LDI r16, 0
            0x5f0f, // SUBI r16, 0xff
            0x3000, // CPI r16, 0
            0xf7e9, // BRNE word 1
            0xcffb, // RJMP word 0
        ];
        byte[] firmware = new byte[words.Length * 2];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                firmware.AsSpan(index * 2),
                words[index]);
        }
        return firmware;
    }

    static void ConfigureIdleLoopState(
        MiaMachine machine,
        int startWord = 0x001587)
    {
        machine.Cpu.PC = startWord;
        machine.Cpu.Data[0x02df0c] = 0x12;
        machine.Cpu.Data[0x00f603] = 2;
        machine.Cpu.Data[16] = 0x32;
        machine.Cpu.Data[machine.Cpu.DirectDataRampRegister] = 0;
        machine.Cpu.SetUint16(30, 0x0ac0);
        machine.Cpu.Data[0x0ac0] = 0x32;
        machine.Cpu.SetStatusRegister(0xa0);
    }

    static void WriteWord(Cpu cpu, int address, ushort value)
    {
        cpu.WriteData(address, (byte)value);
        cpu.WriteData(address + 1, (byte)(value >> 8));
    }
}
