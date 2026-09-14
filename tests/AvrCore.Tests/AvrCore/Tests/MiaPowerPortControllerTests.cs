// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaPowerPortControllerTests
{
    [Fact]
    public void HeldBootKeyAssertsExt1WhenFirmwareUnmasksItsLatchedEdge()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var interrupts = new AsicInterruptController(cpu);
        var ports = new MiaPowerPortController(
            interrupts,
            powerPressedInitially: true);

        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.InterruptMaskCommand, 0xe9]);

        Assert.Equal(1, ports.InterruptCount);
        Assert.Equal(AsicInterruptController.External1Source,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(AsicInterruptController.HighPriorityVectorWord,
            cpu.NextInterrupt);

        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.OnOffStatusCommand]);
        Assert.Equal(0x02,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
        Assert.Equal(0x00,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void MaskedPowerEdgeWaitsUntilFirmwareEnablesIt()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var interrupts = new AsicInterruptController(cpu);
        var ports = new MiaPowerPortController(
            interrupts,
            powerPressedInitially: true);

        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.InterruptMaskCommand, 0xeb]);
        Assert.Equal(0, ports.InterruptCount);
        Assert.Equal(-1, cpu.NextInterrupt);

        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.InterruptMaskCommand, 0xe9]);
        Assert.Equal(1, ports.InterruptCount);
    }

    [Fact]
    public void ReleasedKeyReportsTheActiveLowLevelWithAnEdge()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var interrupts = new AsicInterruptController(cpu);
        var ports = new MiaPowerPortController(
            interrupts,
            powerPressedInitially: true);
        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [MiaPowerPortController.OnOffStatusCommand]);
        Assert.Equal(0x02,
            ports.ReadByte(MiaPowerPortController.ReadAddress));

        ports.ReleasePower();

        Assert.Equal(0x03,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Theory]
    [InlineData(0x90, true)]
    [InlineData(0x91, true)]
    [InlineData(0x92, false)]
    [InlineData(0x72, false)]
    public void OnlyAcknowledgesTheRecoveredPrimaryBusAddress(
        byte address,
        bool expected)
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));

        Assert.Equal(expected, MiaPowerPortController.Acknowledges(address));
    }

    [Theory]
    [InlineData(0xf3, false, 0x00)]
    [InlineData(0xf3, true, 0x04)]
    [InlineData(0xf4, false, 0x20)]
    [InlineData(0xf4, true, 0x24)]
    public void RevisionAndPortStatusComeFromTheSelectedHardwareProfile(
        byte revision,
        bool externalPowerConnected,
        byte expectedPortStatus)
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu),
            externalPowerConnectedInitially: externalPowerConnected,
            siliconRevision: revision);

        Select(ports, MiaPowerPortController.RevisionCommand);
        Assert.Equal(revision,
            ports.ReadByte(MiaPowerPortController.ReadAddress));

        Select(ports, MiaPowerPortController.PortStatusCommand);
        Assert.Equal(expectedPortStatus,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void ExternalPowerInputCanChangeAfterBoot()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu),
            siliconRevision: 0xf4);

        Select(ports, MiaPowerPortController.PortStatusCommand);
        Assert.Equal(
            MiaPowerPortController.PortStatusReady,
            ports.ReadByte(MiaPowerPortController.ReadAddress));

        ports.SetExternalPowerConnected(true);

        Assert.True(ports.ExternalPowerConnected);
        Assert.Equal(
            MiaPowerPortController.PortStatusReady |
            MiaPowerPortController.ExternalPowerLevel,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void UnmaskedExternalPowerEdgeRaisesExt1UntilEdgeStatusIsRead()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        Write(
            ports,
            MiaPowerPortController.InterruptMaskCommand,
            0xe9);

        ports.SetExternalPowerConnected(true);

        Assert.Equal(1, ports.InterruptCount);
        Assert.Equal(AsicInterruptController.External1Source,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Select(ports, MiaPowerPortController.OnOffStatusCommand);
        Assert.Equal(
            MiaPowerPortController.PowerKeyLevel |
            MiaPowerPortController.ExternalPowerLevel,
            ports.ReadByte(MiaPowerPortController.ReadAddress));

        ports.SetExternalPowerConnected(false);

        Assert.Equal(2, ports.InterruptCount);
    }

    [Fact]
    public void FirmwareCanReadBackTheRecoveredControlLatches()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));

        Write(ports, MiaPowerPortController.AdcControlCommand, 0x30);
        Write(ports, MiaPowerPortController.PowerControlCommand, 0xb5);

        Select(ports, MiaPowerPortController.AdcControlCommand);
        Assert.Equal(0x30,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
        Select(ports, MiaPowerPortController.PowerControlCommand);
        Assert.Equal(0xb5,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void AdcSampleRegistersRequireTheFirmwareSelectedBatteryChannel()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu),
            batteryAdcSample: 0x58);

        Write(ports, MiaPowerPortController.AdcControlCommand, 0x30);
        foreach (var selector in new[]
                 {
                     MiaPowerPortController.FirstAdcSampleCommand,
                     MiaPowerPortController.SecondaryAdcSampleCommand,
                     MiaPowerPortController.AlternateAdcSampleCommand,
                     MiaPowerPortController.BatteryAdcSampleCommand,
                 })
        {
            Select(ports, selector);
            Assert.Equal(0x58,
                ports.ReadByte(MiaPowerPortController.ReadAddress));
        }
        Assert.Equal(0, ports.UnsupportedReadCount);
    }

    [Fact]
    public void ChargedProfileExposesRetainedBatteryVoltageOnAdcChannelZero()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu),
            externalPowerConnectedInitially: true,
            externalPowerAdcSample: 0x58);

        Write(ports, MiaPowerPortController.AdcControlCommand, 0x00);
        Select(ports, MiaPowerPortController.BatteryAdcSampleCommand);

        Assert.Equal(
            0x58,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void ChargedProfileExposesPositiveCurrentOnAdcChannelTwo()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu),
            externalPowerConnectedInitially: true,
            chargingCurrentAdcSample: 0x6a);
        var chargingStates = new List<bool>();
        ports.ChargingStateChanged += chargingStates.Add;

        Write(ports, MiaPowerPortController.AdcControlCommand, 0x50);
        Select(ports, MiaPowerPortController.ChargingCurrentAdcSampleCommand);

        Assert.Equal(
            0x6a,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
        Assert.True(ports.ChargingActive);
        Assert.Equal([true], chargingStates);

        ports.SetExternalPowerConnected(false);

        Assert.False(ports.ChargingActive);
        Assert.Equal([true, false], chargingStates);
    }

    [Fact]
    public void BatteryTemperatureUsesAdcChannelThree()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu),
            batteryTemperatureAdcSample: 0x0a);

        Write(ports, MiaPowerPortController.AdcControlCommand, 0x70);
        Select(ports, MiaPowerPortController.BatteryTemperatureAdcSampleCommand);

        Assert.Equal(
            0x0a,
            ports.ReadByte(MiaPowerPortController.ReadAddress));

        Select(ports, MiaPowerPortController.AlternateAdcSampleCommand);
        Assert.Equal(
            0x0a,
            ports.ReadByte(MiaPowerPortController.ReadAddress));
    }

    [Fact]
    public void UnrecoveredAdcChannelFailsClosed()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        Write(ports, MiaPowerPortController.AdcControlCommand, 0x10);
        foreach (var selector in new[]
                 {
                     MiaPowerPortController.FirstAdcSampleCommand,
                     MiaPowerPortController.SecondaryAdcSampleCommand,
                     MiaPowerPortController.AlternateAdcSampleCommand,
                     MiaPowerPortController.BatteryAdcSampleCommand,
                 })
        {
            Select(ports, selector);
            var error = Assert.Throws<InvalidOperationException>(() =>
                ports.ReadByte(MiaPowerPortController.ReadAddress));
            Assert.Contains("unrecovered channel", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(4, ports.UnsupportedReadCount);
    }

    [Fact]
    public void UnrecoveredReadSelectorFailsClosed()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        Select(ports, 0xbe);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ports.ReadByte(MiaPowerPortController.ReadAddress));

        Assert.Contains("selector 0xbe", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, ports.UnsupportedReadCount);
    }

    [Fact]
    public void UnrecoveredWriteSelectorFailsClosed()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));

        var error = Assert.Throws<InvalidOperationException>(() =>
            Write(ports, 0xbe, 0x12));

        Assert.Contains("selector 0xbe", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, ports.RejectedWriteCount);
    }

    [Fact]
    public void EveryObservedWriteOnlyLatchIsAccepted()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        byte[] selectors =
        [
            MiaPowerPortController.PortA3OutputCommand,
            MiaPowerPortController.PortA4OutputCommand,
            MiaPowerPortController.PortA5OutputCommand,
            MiaPowerPortController.PortA6OutputCommand,
            MiaPowerPortController.PortA7OutputCommand,
            MiaPowerPortController.PortA9OutputCommand,
            MiaPowerPortController.PortB0OutputCommand,
        ];

        foreach (var selector in selectors)
        {
            Write(ports, selector, selector);
        }

        Assert.Equal(selectors.Length, ports.WriteCount);
        Assert.Equal(0, ports.RejectedWriteCount);
    }

    [Fact]
    public void OutputStatePublishesOnlyEffectiveLatchChanges()
    {
        var cpu = new Cpu(new byte[0x100], 0x10000);
        var ports = new MiaPowerPortController(
            new AsicInterruptController(cpu));
        var observations = new List<MiaPowerPortOutputState>();
        ports.OutputStateChanged += observations.Add;

        Write(ports, MiaPowerPortController.PortA3OutputCommand, 0x14);
        Write(ports, MiaPowerPortController.PortA3OutputCommand, 0x14);
        Write(ports, MiaPowerPortController.PortA5OutputCommand, 0x0d);
        Write(ports, MiaPowerPortController.PowerControlCommand, 0x72);

        Assert.Equal(3, observations.Count);
        Assert.Equal(new MiaPowerPortOutputState(
            0x14, 0, 0, 0, 0, 0, 0, 0), observations[0]);
        Assert.Equal(new MiaPowerPortOutputState(
            0x14, 0, 0x0d, 0, 0, 0, 0, 0), observations[1]);
        Assert.Equal(new MiaPowerPortOutputState(
            0x14, 0, 0x0d, 0, 0, 0, 0x72, 0), observations[2]);
        Assert.Equal(observations[^1], ports.OutputState);
        Assert.Equal(4, ports.WriteCount);
    }

    static void Select(MiaPowerPortController ports, byte selector) =>
        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [selector]);

    static void Write(
        MiaPowerPortController ports,
        byte selector,
        byte value) =>
        ports.WriteTransaction(
            MiaPowerPortController.WriteAddress,
            [selector, value]);
}
