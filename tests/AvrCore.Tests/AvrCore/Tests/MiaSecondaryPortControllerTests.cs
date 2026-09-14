// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaSecondaryPortControllerTests
{
    [Fact]
    public void ResetStateContainsNoInventedLatchValues()
    {
        var ports = new MiaSecondaryPortController();

        Assert.Equal(default, ports.OutputState);
        Assert.Equal(0, ports.ReadCount);
        Assert.Equal(0, ports.WriteCount);
        Assert.Equal(0, ports.OpaqueWriteCount);
        Assert.Equal(0, ports.UnsupportedReadCount);
        Assert.Equal(0, ports.RejectedWriteCount);
    }

    [Theory]
    [InlineData(0x92, true)]
    [InlineData(0x93, true)]
    [InlineData(0x90, false)]
    [InlineData(0x94, false)]
    public void AcknowledgesOnlyRecoveredAddressPair(byte address, bool expected)
    {
        var ports = new MiaSecondaryPortController();

        Assert.Equal(expected, MiaSecondaryPortController.Acknowledges(address));
    }

    [Theory]
    [InlineData(0x41)]
    [InlineData(0x42)]
    [InlineData(0x44)]
    [InlineData(0x45)]
    [InlineData(0x61)]
    [InlineData(0x66)]
    [InlineData(0x91)]
    [InlineData(0x98)]
    [InlineData(0xa1)]
    [InlineData(0xa4)]
    public void ReturnsOnlyFirmwareAcceptedIdentityProfiles(byte identity)
    {
        var ports = new MiaSecondaryPortController(
            new(identity, Status: 0x5a));

        Select(ports, MiaSecondaryPortController.IdentitySelector);
        Assert.Equal(identity,
            ports.ReadByte(MiaSecondaryPortController.ReadAddress));
        Select(ports, MiaSecondaryPortController.StatusSelector);
        Assert.Equal(0x5a,
            ports.ReadByte(MiaSecondaryPortController.ReadAddress));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x43)]
    [InlineData(0x46)]
    [InlineData(0x60)]
    [InlineData(0x67)]
    [InlineData(0x90)]
    [InlineData(0x99)]
    [InlineData(0xa0)]
    [InlineData(0xa5)]
    [InlineData(0xff)]
    public void RejectsIdentityProfilesRejectedByFirmware(byte identity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MiaSecondaryPortController(new(identity, Status: 0)));
    }

    [Fact]
    public void RetainsOnlyRecoveredOutputRegistersAndPublishesChanges()
    {
        var ports = new MiaSecondaryPortController();
        var observations = new List<MiaSecondaryPortOutputState>();
        ports.OutputStateChanged += observations.Add;

        Write(ports, MiaSecondaryPortController.Output40Register, 0x21);
        Write(ports, MiaSecondaryPortController.Output40Register, 0x21);
        Write(ports, MiaSecondaryPortController.Output48Register, 0xa0);
        Write(ports, MiaSecondaryPortController.Output80Register, 0x07);

        Assert.Equal(3, observations.Count);
        Assert.Equal(new(0x21, 0x00, 0x00), observations[0]);
        Assert.Equal(new(0x21, 0xa0, 0x00), observations[1]);
        Assert.Equal(new(0x21, 0xa0, 0x07), observations[2]);
        Assert.Equal(observations[^1], ports.OutputState);
        Assert.Equal(4, ports.WriteCount);
    }

    [Fact]
    public void OpaqueWritesAreAcknowledgedLoggedAndHaveNoInventedState()
    {
        var ports = new MiaSecondaryPortController();
        var writes = new List<(byte Register, byte Value)>();
        ports.OpaqueRegisterWritten +=
            (register, value) => writes.Add((register, value));

        Write(ports, 0x42, 0x0c);
        Write(ports, 0x86, 0x01);

        Assert.Equal([(0x42, 0x0c), (0x86, 0x01)], writes);
        Assert.Equal(default, ports.OutputState);
        Assert.Equal(2, ports.OpaqueWriteCount);
        Assert.Equal(2, ports.WriteCount);
    }

    [Fact]
    public void UnknownReadSelectorFailsClosed()
    {
        var ports = new MiaSecondaryPortController();
        Select(ports, MiaSecondaryPortController.Output40Register);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ports.ReadByte(MiaSecondaryPortController.ReadAddress));

        Assert.Contains("selector 0x40", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, ports.UnsupportedReadCount);
    }

    [Fact]
    public void RepeatedStartReadUsesTheSecondaryDeviceSelector()
    {
        const int dataAddress = 0x0839;
        var cpu = new Cpu(new byte[0x100], 0x1000000);
        var clock = new MiaSystemClock();
        var interrupts = new AsicInterruptController(cpu);
        var ports = new MiaSecondaryPortController(
            new(Identity: 0x66, Status: 0));
        _ = new AsicI2cController(
            cpu,
            clock,
            interrupts,
            dataAddress,
            AsicInterruptController.I2cSource,
            MiaSecondaryPortController.Acknowledges,
            ports.WriteTransaction,
            ports.ReadByte);

        Start(cpu, clock, dataAddress);
        Step(cpu, clock, dataAddress, MiaSecondaryPortController.WriteAddress);
        Step(cpu, clock, dataAddress, MiaSecondaryPortController.IdentitySelector);
        Start(cpu, clock, dataAddress);
        Assert.Equal(0x10, cpu.ReadData(dataAddress + 2));
        Step(cpu, clock, dataAddress, MiaSecondaryPortController.ReadAddress);
        cpu.WriteData(dataAddress + 1, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);

        Assert.Equal(0x58, cpu.ReadData(dataAddress + 2));
        Assert.Equal(0x66, cpu.ReadData(dataAddress));
        Assert.Equal(1, ports.ReadCount);
    }

    static void Select(MiaSecondaryPortController ports, byte selector) =>
        ports.WriteTransaction(
            MiaSecondaryPortController.WriteAddress,
            [selector]);

    static void Write(
        MiaSecondaryPortController ports,
        byte register,
        byte value) =>
        ports.WriteTransaction(
            MiaSecondaryPortController.WriteAddress,
            [register, value]);

    static void Start(Cpu cpu, MiaSystemClock clock, int dataAddress)
    {
        cpu.WriteData(dataAddress + 1, 0xa0);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
    }

    static void Step(
        Cpu cpu,
        MiaSystemClock clock,
        int dataAddress,
        byte value)
    {
        cpu.WriteData(dataAddress, value);
        cpu.WriteData(dataAddress + 1, 0x80);
        clock.AdvanceBy(AsicI2cController.CompletionCycles);
    }
}
