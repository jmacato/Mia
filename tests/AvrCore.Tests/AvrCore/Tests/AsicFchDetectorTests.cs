// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicFchDetectorTests
{
    [Fact]
    public void NoSignalAttemptClearsStaleResultsAtFirmwareReadCommand()
    {
        var cpu = CreateCpu();
        var detector = new AsicFchDetector(cpu, new MiaSystemClock());
        cpu.Data.AsSpan(
            AsicFchDetector.FirstResultAddress,
            AsicFchDetector.ResultLength).Fill(0x5a);

        StartAttempt(cpu);

        Assert.Equal(1, detector.StartedCount);
        Assert.Equal(
            Enumerable.Repeat((byte)0x5a, AsicFchDetector.ResultLength),
            ReadResults(cpu));

        cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ReadResultCommand);

        Assert.Equal(1, detector.CompletedCount);
        Assert.Equal(0, detector.SuccessfulCount);
        Assert.Equal(new byte[AsicFchDetector.ResultLength], ReadResults(cpu));
    }

    [Fact]
    public void SuccessfulAttemptPublishesOnlySevenSourceDerivedBytes()
    {
        var cpu = CreateCpu();
        var clock = new MiaSystemClock();
        var rf = new AsicRfFrontend(cpu);
        var source = new AsicFchDetectorTestsCapturingSource([1, 2, 3, 4, 5, 6, 7]);
        var detector = new AsicFchDetector(cpu, clock, rf, source);
        ProgramRfTransaction(cpu, slot: 3);
        clock.AdvanceBy(123);

        StartAttempt(cpu);
        cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ReadResultCommand);

        Assert.Equal(1, detector.SuccessfulCount);
        Assert.NotNull(source.Request);
        Assert.Equal(123, source.Request!.StartCycle);
        var transaction = Assert.Single(source.Request.RfTransactions);
        Assert.Equal(3, transaction.Slot);
        var expectedTransaction = Enumerable
            .Range(1, AsicRfFrontend.TransactionSize)
            .Select(value => (byte)value)
            .ToArray();
        expectedTransaction[0] = 3;
        Assert.Equal(expectedTransaction, transaction.Bytes.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, ReadResults(cpu));
    }

    [Theory]
    [InlineData(0x06, 0x7f)]
    [InlineData(0x05, 0x7f)]
    [InlineData(0x06, 0x7e)]
    public void IncompleteStartSequenceIsInert(byte command, byte parameter)
    {
        var cpu = CreateCpu();
        var detector = new AsicFchDetector(cpu, new MiaSystemClock());

        cpu.WriteData(AsicFchDetector.CommandAddress, command);
        cpu.WriteData(AsicFchDetector.ParameterAddress, parameter);
        cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ReadResultCommand);

        Assert.Equal(0, detector.StartedCount);
        Assert.Equal(0, detector.CompletedCount);
    }

    [Fact]
    public void InvalidSuccessLengthFailsClosed()
    {
        var cpu = CreateCpu();
        var detector = new AsicFchDetector(
            cpu,
            new MiaSystemClock(),
            source: new AsicFchDetectorTestsCapturingSource([1, 2, 3]));
        cpu.Data.AsSpan(
            AsicFchDetector.FirstResultAddress,
            AsicFchDetector.ResultLength).Fill(0x5a);

        StartAttempt(cpu);
        cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ReadResultCommand);

        Assert.Equal(0, detector.SuccessfulCount);
        Assert.Equal(new byte[AsicFchDetector.ResultLength], ReadResults(cpu));
    }

    [Fact]
    public void MachineRoutesFchWritesAndInjectedSource()
    {
        var source = new AsicFchDetectorTestsCapturingSource([8, 9, 10, 11, 12, 13, 14]);
        using var machine = new MiaMachine(
            [0x00, 0x00],
            virtualSim: false,
            powerPressedInitially: false,
            powerKeyReleaseCycle: long.MaxValue,
            fchSource: source);

        StartAttempt(machine.Cpu);
        machine.Cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ReadResultCommand);

        Assert.Equal(1, machine.FchDetector.CompletedCount);
        Assert.Equal(
            new byte[] { 8, 9, 10, 11, 12, 13, 14 },
            ReadResults(machine.Cpu));
    }

    static Cpu CreateCpu() => new(new byte[0x100], 0x200000);

    static void StartAttempt(Cpu cpu)
    {
        cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.ArmCommand);
        cpu.WriteData(
            AsicFchDetector.CommandAddress,
            AsicFchDetector.StartCommand);
        cpu.WriteData(
            AsicFchDetector.ParameterAddress,
            AsicFchDetector.StartParameter);
    }

    static byte[] ReadResults(Cpu cpu) => cpu.Data.AsSpan(
        AsicFchDetector.FirstResultAddress,
        AsicFchDetector.ResultLength).ToArray();

    static void ProgramRfTransaction(Cpu cpu, byte slot)
    {
        for (var index = 0; index < AsicRfFrontend.TransactionSize; index++)
        {
            cpu.WriteData(
                AsicRfFrontend.FirstTransactionAddress + index,
                index == 0 ? slot : (byte)(index + 1));
        }
    }
}
