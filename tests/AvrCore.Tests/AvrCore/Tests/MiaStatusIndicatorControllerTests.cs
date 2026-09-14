// SPDX-License-Identifier: MIT

using Arm7Core;
using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaStatusIndicatorControllerTests
{
    [Fact]
    public void NetworkGreenTracksRegistrationAndNativeServingStates()
    {
        var indicators = new MiaStatusIndicatorController();
        var transitions = new List<MiaStatusLedState>();
        indicators.StateChanged += transitions.Add;

        indicators.ObserveNetworkBoundary(registered: false, nativeMphState: 1);
        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 1);
        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 2);
        Assert.False(indicators.State.NetworkGreen);

        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 3);
        Assert.True(indicators.State.NetworkGreen);
        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 6);
        Assert.True(indicators.State.NetworkGreen);
        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 0x0b);
        Assert.True(indicators.State.NetworkGreen);

        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 8);
        Assert.False(indicators.State.NetworkGreen);
        indicators.ObserveNetworkBoundary(registered: true, nativeMphState: 0x12);
        Assert.False(indicators.State.NetworkGreen);
        indicators.ObserveNetworkBoundary(registered: false, nativeMphState: 3);
        Assert.False(indicators.State.NetworkGreen);

        Assert.Equal(2, transitions.Count);
        Assert.True(transitions[0].NetworkGreen);
        Assert.False(transitions[1].NetworkGreen);
        Assert.Equal(2, indicators.Diagnostics.NetworkStateChangeCount);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    public void ExactBluetoothOperationRecordProducesOnlyAnActivityPulse(
        byte operationValue)
    {
        var indicators = new MiaStatusIndicatorController();

        FeedAsicFrame(
            indicators,
            MiaStatusIndicatorController.BluetoothControllerDestination,
            MiaStatusIndicatorController.BluetoothApplicationSource,
            [0xde, 0xd2, operationValue, 0, 0, 0]);

        Assert.True(indicators.State.BluetoothBlue);
        var diagnostics = indicators.Diagnostics;
        Assert.Equal(1, diagnostics.BluetoothControllerRecordCount);
        Assert.Equal(1, diagnostics.BluetoothOperationRequestCount);
        Assert.Equal(operationValue, diagnostics.LastBluetoothOperationValue);

        const long observationCycle = 12_000_000;
        indicators.SynchronizeTestClock(observationCycle);
        indicators.SynchronizeTestClock(
            observationCycle +
            MiaStatusIndicatorController.BluetoothActivityDurationCycles - 1);
        Assert.True(indicators.State.BluetoothBlue);
        indicators.SynchronizeTestClock(
            observationCycle +
            MiaStatusIndicatorController.BluetoothActivityDurationCycles);
        Assert.False(indicators.State.BluetoothBlue);

        // Operation value 1 is the dynamically observed automatic-mode value,
        // but idle is not synthesized as continuous Bluetooth activity.
        Assert.False(indicators.State.BluetoothBlue);
    }

    [Fact]
    public void CompletionAndActualDspTrafficRetriggerBluetoothActivity()
    {
        var indicators = new MiaStatusIndicatorController();

        foreach (byte value in Convert.FromHexString("DC03000606D300"))
        {
            indicators.ObserveModemToAsicByte(value);
        }

        Assert.True(indicators.State.BluetoothBlue);
        Assert.Equal(
            1,
            indicators.Diagnostics.BluetoothOperationCompletionCount);
        indicators.SynchronizeTestClock(100);
        indicators.SynchronizeTestClock(
            100 + MiaStatusIndicatorController.BluetoothActivityDurationCycles);
        Assert.False(indicators.State.BluetoothBlue);

        indicators.ObserveBluetoothDspPacket(new(0x10, [0x03]));
        Assert.True(indicators.State.BluetoothBlue);
        Assert.Equal(1, indicators.Diagnostics.BluetoothDspPacketCount);
        indicators.SynchronizeTestClock(5_000_000);
        indicators.ObserveBluetoothDspTransfer(new(0, 7, 3, [0x0c, 0x30, 0x2f]));
        Assert.Equal(1, indicators.Diagnostics.BluetoothDspTransferCount);
        indicators.SynchronizeTestClock(6_000_000);
        indicators.SynchronizeTestClock(
            6_000_000 +
            MiaStatusIndicatorController.BluetoothActivityDurationCycles);
        Assert.False(indicators.State.BluetoothBlue);
    }

    [Fact]
    public void AttachedBluetoothActivityExpiresFromAClockEvent()
    {
        var clock = new MiaSystemClock();
        using var clockWorker = new MiaWorker("status-clock-test");
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        using var bluetooth = new ArmModemBluetoothPeripheral(bus);
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var powerPorts = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        var indicators = new MiaStatusIndicatorController();
        indicators.Attach(new MiaStatusIndicatorDependencies(
            bus,
            bluetooth,
            powerPorts,
            clock,
            clockWorker));

        // Dispatched on clockWorker so the flush below only has to wait out
        // one hop (QueueBluetoothActivitySchedule's own worker.Post), not two.
        clockWorker.Invoke(() =>
            indicators.ObserveBluetoothDspPacket(new(0x10, [0x03])));
        clockWorker.Invoke(() => { });
        clock.AdvanceBy(
            MiaStatusIndicatorController.BluetoothActivityDurationCycles - 1);
        Assert.True(indicators.State.BluetoothBlue);

        clock.AdvanceBy(1);

        Assert.False(indicators.State.BluetoothBlue);
        indicators.Detach();
    }

    [Fact]
    public void NativeControllerCommandRingCollectsGlobalAndPerLinkStates()
    {
        var indicators = new MiaStatusIndicatorController();

        indicators.ObserveBluetoothControllerCommand(new(0x10, [0x03]));
        indicators.ObserveBluetoothControllerCommand(new(0x25, [0x00]));
        indicators.ObserveBluetoothControllerCommand(new(0x25, [0x80]));
        indicators.ObserveBluetoothControllerCommand(new(0x85, [0x01]));
        indicators.ObserveBluetoothControllerCommand(new(0x85, [0x03]));
        indicators.ObserveBluetoothControllerCommand(new(0xf5, [0x00]));

        // Malformed state commands and unrelated type-x5 commands are not
        // lifecycle samples.
        indicators.ObserveBluetoothControllerCommand(new(0x25, []));
        indicators.ObserveBluetoothControllerCommand(new(0x85, [0x03, 0x00]));
        indicators.ObserveBluetoothControllerCommand(new(0x75, [0x03]));

        var diagnostics = indicators.Diagnostics;
        var states = diagnostics.BluetoothControllerStates;
        Assert.True(states.HasGlobalState);
        Assert.Equal(0x80, states.GlobalState);
        Assert.Equal(2, diagnostics.BluetoothGlobalStateCommandCount);
        Assert.Equal(3, diagnostics.BluetoothLinkStateCommandCount);
        Assert.Equal(0x81, states.ObservedLinkSlotMask);
        Assert.True(states.TryGetLinkState(0, out byte firstLinkState));
        Assert.Equal(0x03, firstLinkState);
        Assert.False(states.TryGetLinkState(1, out _));
        Assert.True(states.TryGetLinkState(7, out byte lastLinkState));
        Assert.Equal(0x00, lastLinkState);
        Assert.Equal(0, diagnostics.BluetoothDspPacketCount);
    }

    [Fact]
    public void NativeChargingAndBluetoothOnHoldBlueSteady()
    {
        var indicators = new MiaStatusIndicatorController();

        indicators.ObserveChargingBoundary(active: true);
        indicators.ObserveBluetoothControllerCommand(new(
            MiaStatusIndicatorController.BluetoothGlobalStateCommandType,
            [MiaStatusIndicatorController.BluetoothGlobalOnState]));

        var diagnostics = indicators.Diagnostics;
        Assert.True(diagnostics.ChargingActive);
        Assert.True(diagnostics.BluetoothSteadyDueToCharging);
        Assert.True(diagnostics.State.BluetoothBlue);

        const long observationCycle = 12_000_000;
        indicators.SynchronizeTestClock(observationCycle);
        indicators.SynchronizeTestClock(
            observationCycle +
            MiaStatusIndicatorController.BluetoothActivityDurationCycles * 2);
        Assert.True(indicators.State.BluetoothBlue);

        indicators.ObserveChargingBoundary(active: false);

        diagnostics = indicators.Diagnostics;
        Assert.False(diagnostics.ChargingActive);
        Assert.False(diagnostics.BluetoothSteadyDueToCharging);
        Assert.False(diagnostics.State.BluetoothBlue);
    }

    [Fact]
    public void UnrelatedNativeLinkTrafficDoesNotIlluminateBluetoothBlue()
    {
        var indicators = new MiaStatusIndicatorController();

        FeedAsicFrame(
            indicators,
            destination: 0x85,
            source: 0x5c,
            [0xde, 0xd2, 1, 0, 0, 0]);
        foreach (byte value in Convert.FromHexString("DC03000506D300"))
        {
            indicators.ObserveModemToAsicByte(value);
        }

        Assert.Equal(default, indicators.State);
        Assert.Equal(0, indicators.Diagnostics.BluetoothControllerRecordCount);
        Assert.Equal(0, indicators.Diagnostics.BluetoothOperationCompletionCount);
    }

    [Fact]
    public void PowerPortAndSecondaryLatchChangesNeverDriveStatusIndicators()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var powerPorts = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        var secondaryPorts = new MiaSecondaryPortController();
        var indicators = new MiaStatusIndicatorController();

        WritePowerPort(powerPorts, 0xa5, 0x01);
        WritePowerPort(powerPorts, 0xa5, 0x80);
        WritePowerPort(powerPorts, 0xa5, 0x81);
        WritePowerPort(powerPorts, 0xa3, 0xff);
        WritePowerPort(powerPorts, 0xaa, 0xff);
        WriteSecondaryPort(secondaryPorts, 0x40, 0x21);
        WriteSecondaryPort(secondaryPorts, 0x40, 0x3f);
        WriteSecondaryPort(secondaryPorts, 0x48, 0xf0);
        WriteSecondaryPort(secondaryPorts, 0x80, 0xff);

        Assert.Equal(default, indicators.State);
        Assert.Equal(0, indicators.Diagnostics.NetworkStateChangeCount);
        Assert.Equal(0, indicators.Diagnostics.BluetoothControllerRecordCount);
    }

    static void FeedAsicFrame(
        MiaStatusIndicatorController indicators,
        byte destination,
        byte source,
        byte[] payload)
    {
        var frame = new byte[6 + payload.Length];
        frame[0] = 0xab;
        frame[1] = 0xba;
        frame[2] = destination;
        frame[3] = (byte)payload.Length;
        frame[4] = (byte)(payload.Length >> 8);
        frame[5] = source;
        payload.CopyTo(frame, 6);
        foreach (byte value in frame)
        {
            indicators.ObserveAsicToModemByte(value);
        }
    }

    static void WritePowerPort(
        MiaPowerPortController ports,
        byte selector,
        byte value) =>
        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [selector, value]);

    static void WriteSecondaryPort(
        MiaSecondaryPortController ports,
        byte register,
        byte value) =>
        ports.WriteTransaction(
            MiaSecondaryPortController.WriteAddress,
            [register, value]);

}
