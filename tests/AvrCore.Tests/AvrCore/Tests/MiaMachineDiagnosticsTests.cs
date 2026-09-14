// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaMachineDiagnosticsTests
{
    [Fact]
    public void InitiallyReleasedPowerIsReleasedAtBothHardwareBoundaries()
    {
        using var machine = CreateLoopingMachine(powerPressedInitially: false);

        Assert.False(machine.PowerPorts.PowerPressed);
        machine.Cpu.WriteData(
            AsicKeypad.ScanControlAddress,
            AsicKeypad.NoPowerScanMask);
        Assert.Equal(
            AsicKeypad.IdleRows,
            machine.Cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void ExecutionObserverStopsBeforeTheRequestedWorkItemOnTheAvrWorker()
    {
        using var machine = CreateLoopingMachine();
        var observer = new MiaMachineDiagnosticsTestsRecordingExecutionObserver
        {
            Before = _ => "diagnostic stop",
            StopBeforeCall = 3,
        };
        machine.Diagnostics.ExecutionObserver = observer;

        Assert.Equal(3, machine.RunWorkItems(10));

        Assert.True(machine.IsStopped);
        Assert.Equal("diagnostic stop", machine.StopReason);
        Assert.Equal(3, observer.BeforeCalls);
        Assert.Equal(2, machine.ExecutedInstructions);
        Assert.All(
            observer.ThreadIds,
            threadId => Assert.Equal(AvrWorker(machine).ThreadId, threadId));
    }

    [Fact]
    public void ExecutionObserverSeesRomResultBeforeTheMachineStops()
    {
        byte[] firmware = Assembler.Assemble("JMP 0x7c0000").Bytes;
        using var machine = new MiaMachine(
            firmware,
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue);
        var observer = new MiaMachineDiagnosticsTestsRecordingExecutionObserver();
        machine.Diagnostics.ExecutionObserver = observer;

        machine.RunWorkItems(10);

        var dispatch = Assert.Single(observer.RomDispatches);
        Assert.Equal(AsicRom.StartWord, dispatch.Entry);
        Assert.Equal(AsicRomDispatchResult.Unknown, dispatch.Result);
        Assert.False(dispatch.WasStopped);
        Assert.Equal(AvrWorker(machine).ThreadId, dispatch.ThreadId);
        Assert.True(machine.IsStopped);
        Assert.Contains("unimplemented ROM service", machine.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsolutePowerAndKeyStimuliRunOnTheInputWorker()
    {
        using var machine = CreateLoopingMachine(powerPressedInitially: false);
        var inputWorker = Assert.Single(
            machine.Workers,
            worker => worker.Name == "keypad/input peripheral");
        long workBefore = inputWorker.CompletedWorkCount;
        const long powerCycle = 5;
        const long keyCycle = 9;

        machine.Diagnostics.SchedulePowerKeyAt(powerCycle, pressed: true);
        machine.Diagnostics.ScheduleKeyAt(
            keyCycle,
            scanMask: 0x0e,
            rowMask: 0x04,
            secondaryScanMask: null,
            pressed: true);

        RunUntilCycle(machine, powerCycle);
        Assert.True(machine.PowerPorts.PowerPressed);

        RunUntilCycle(machine, keyCycle);
        Assert.True(inputWorker.CompletedWorkCount >= workBefore + 2);
        machine.Cpu.WriteData(AsicKeypad.ScanControlAddress, 0x0e);
        Assert.Equal(
            (byte)(AsicKeypad.IdleRows & ~0x04),
            machine.Cpu.ReadData(AsicKeypad.RowStateAddress));
    }

    [Fact]
    public void AbsoluteSimBurstIsQueuedOnlyWhenItsWorkerDeadlineArrives()
    {
        using var machine = CreateLoopingMachine();
        var simWorker = Assert.Single(
            machine.Workers,
            worker => worker.Name == "SIM UART peripheral");
        const long arrivalCycle = 9;
        byte[] burst = [0x3b, 0x9f, 0x95];

        machine.Diagnostics.ScheduleSimReceiveAt(arrivalCycle, burst);
        while (machine.Cycles < arrivalCycle - 3)
        {
            machine.RunWorkItems(1);
        }
        Assert.Equal(0, machine.SimInterface.QueuedByteCount);
        long workBeforeArrival = simWorker.CompletedWorkCount;

        RunUntilCycle(machine, arrivalCycle);

        Assert.Equal(burst.Length, machine.SimInterface.QueuedByteCount);
        Assert.True(simWorker.CompletedWorkCount > workBeforeArrival);
    }

    [Fact]
    public void DataProbesChainFirmwareAccessesAndSnapshotsAreStableCopies()
    {
        byte[] firmware = Assembler.Assemble(
            "LDI r16, 0x5a\n" +
            "STS 0x1234, r16\n" +
            "LDS r17, 0x1234\n" +
            "LDI r16, 0xa5\n" +
            "STS 0x1234, r16\n" +
            "loop: RJMP loop").Bytes;
        using var machine = new MiaMachine(
            firmware,
            virtualSim: false,
            powerKeyReleaseCycle: long.MaxValue);
        var reads = new List<MiaDataReadObservation>();
        var writes = new List<MiaDataWriteObservation>();
        var probeThreads = new List<int>();
        machine.Diagnostics.InstallDataReadProbe(0x1234, observation =>
        {
            reads.Add(observation);
            probeThreads.Add(Environment.CurrentManagedThreadId);
        });
        machine.Diagnostics.InstallDataWriteProbe(0x1234, observation =>
        {
            writes.Add(observation);
            probeThreads.Add(Environment.CurrentManagedThreadId);
        });

        Assert.Equal(3, machine.RunWorkItems(3));
        byte[] firstDataSnapshot = machine.Diagnostics.SnapshotData(0x1234, 1);
        byte[] programSnapshot = machine.Diagnostics.SnapshotProgram(
            0,
            firmware.Length);

        var firstWrite = Assert.Single(writes);
        Assert.Equal(0x1234, firstWrite.Address);
        Assert.Equal(0x00, firstWrite.OldValue);
        Assert.Equal(0x5a, firstWrite.NewValue);
        Assert.Equal(0xff, firstWrite.Mask);
        var read = Assert.Single(reads);
        Assert.Equal(0x1234, read.Address);
        Assert.Equal(0x5a, read.Value);
        Assert.Equal(new byte[] { 0x5a }, firstDataSnapshot);
        Assert.Equal(firmware, programSnapshot);
        Assert.All(
            probeThreads,
            threadId => Assert.Equal(AvrWorker(machine).ThreadId, threadId));

        Assert.Equal(2, machine.RunWorkItems(2));

        Assert.Equal(2, writes.Count);
        Assert.Equal(0xa5, writes[1].NewValue);
        Assert.Equal(new byte[] { 0x5a }, firstDataSnapshot);
        Assert.Equal(
            new byte[] { 0xa5 },
            machine.Diagnostics.SnapshotData(0x1234, 1));
    }

    [Fact]
    public void DisplayObservationsArePostedToTheHostOutputWorker()
    {
        using var machine = CreateLoopingMachine();
        var hostOutputWorker = Assert.Single(
            machine.Workers,
            worker => worker.Name == "host output");
        var callbackThread = 0;
        var observed = new MiaDisplayTransactionObservation(
            S4595Display.WriteAddress,
            new byte[] { S4595Display.ScanRowCommand, 0x5a },
            new byte[S4595Display.Width * S4595Display.Height],
            Cycle: 42,
            Pc: 0x1234,
            SchedulerRecord: 0,
            DmaCycle: 0,
            DmaPc: 0,
            DmaSource: 0,
            DmaLength: 0);
        machine.Diagnostics.DisplayTransactionObserved += observation =>
        {
            Assert.Equal(observed, observation);
            callbackThread = Environment.CurrentManagedThreadId;
        };

        machine.Diagnostics.PublishDisplayTransaction(observed);
        machine.Diagnostics.FlushHostEvents();

        Assert.Equal(hostOutputWorker.ThreadId, callbackThread);
    }

    static MiaMachine CreateLoopingMachine(bool powerPressedInitially = true) =>
        new(
            [0xff, 0xcf], // RJMP .
            virtualSim: false,
            powerPressedInitially: powerPressedInitially,
            powerKeyReleaseCycle: long.MaxValue);

    static MiaWorker AvrWorker(MiaMachine machine) =>
        Assert.Single(machine.Workers, worker => worker.Name == "AVR core");

    static void RunUntilCycle(MiaMachine machine, long cycle)
    {
        while (machine.Cycles < cycle && !machine.IsStopped)
        {
            machine.RunWorkItems(1);
        }
        Assert.False(machine.IsStopped, machine.StopReason);
        Assert.True(machine.Cycles >= cycle);
    }
}
