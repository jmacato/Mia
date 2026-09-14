// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicRfFrontendTests
{
    [Fact]
    public void FinalOrderedWriteCommitsOneSelectorLocalTransaction()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var frontend = new AsicRfFrontend(cpu);
        var bytes = Convert.FromHexString("00300046000000907790770100F907");

        WriteTransaction(cpu, bytes);

        Assert.Equal(1, frontend.CommittedTransactionCount);
        Assert.True(frontend.TryGetTransaction(0, out var transaction));
        Assert.NotNull(transaction);
        Assert.Equal(bytes, transaction.Bytes.ToArray());
        Assert.True(transaction.HasDuplicatedTuneWord);
    }

    [Fact]
    public void OutOfOrderWritesFailClosedWithoutCommitting()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var frontend = new AsicRfFrontend(cpu);

        cpu.WriteData(AsicRfFrontend.FirstTransactionAddress, 2);
        cpu.WriteData(AsicRfFrontend.FirstTransactionAddress + 2, 0x30);
        for (var address = AsicRfFrontend.FirstTransactionAddress + 3;
             address <= AsicRfFrontend.LastTransactionAddress;
             address++)
        {
            cpu.WriteData(address, 0);
        }

        Assert.Equal(0, frontend.CommittedTransactionCount);
        Assert.False(frontend.TryGetTransaction(2, out _));
    }

    [Theory]
    [InlineData("00300046000000907790770100F907", -49)]
    [InlineData("00300046000000587858780100F907", -44)]
    public void BandZeroTuneWordDecodesToRequestedArfcn(
        string hex,
        short expectedArfcn)
    {
        var transaction = new AsicRfTransaction(0, Convert.FromHexString(hex));

        Assert.True(AsicRfFrontend.TryDecodeBandZeroArfcn(
            0,
            transaction,
            out var arfcn));
        Assert.Equal(expectedArfcn, arfcn);
        Assert.False(AsicRfFrontend.TryDecodeBandZeroArfcn(
            0x40,
            transaction,
            out _));
    }

    [Fact]
    public void AdcActionUsesTheRfTransactionWithTheSameTgSlot()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var source = new AsicRfFrontendTestsRecordingSignalSource();
        var frontend = new AsicRfFrontend(cpu, source);
        WriteTransaction(
            cpu,
            Convert.FromHexString("02300046000000B877B8770100FF07"));
        var action = new AsicTimeGeneratorActionExecution(
            ProgramSelector: 2,
            SchedulePortAddress: 0x0883,
            ActionId: 56,
            Operand: 673,
            QuarterBit: 3348,
            OccurrenceIndex: 1,
            OccurrenceCount: 2);

        var result = frontend.ReadRawSample(2, action);

        Assert.Equal(0x1234, result);
        Assert.NotNull(source.Transaction);
        Assert.Equal((byte)2, source.Transaction.Slot);
        Assert.Equal((byte)2, source.AdcSelector);
    }

    [Fact]
    public void SingleChannelSourceReturnsPowerOnlyForTheDecodedArfcn()
    {
        var source = new AsicSingleChannelRfSource(-49, 0x1234);
        var matching = new AsicRfTransaction(
            0,
            Convert.FromHexString("00300046000000907790770100F907"));
        var other = new AsicRfTransaction(
            0,
            Convert.FromHexString("00300046000000587858780100F907"));
        var action = new AsicTimeGeneratorActionExecution(
            ProgramSelector: 0,
            SchedulePortAddress: 0x0881,
            ActionId: 52,
            Operand: 0,
            QuarterBit: 0,
            OccurrenceIndex: 1,
            OccurrenceCount: 2);

        Assert.Equal(0x1234, source.ReadRawSample(0, matching, action));
        Assert.Equal(0, source.ReadRawSample(0, other, action));
        Assert.Equal(2, source.DecodedSampleCount);
        Assert.Equal(1, source.MatchedSampleCount);
    }

    [Fact]
    public void SingleChannelSourceRejectsUnknownSelectorBankAndArfcn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AsicSingleChannelRfSource(125, 1));
        var source = new AsicSingleChannelRfSource(-49, 0x1234);
        var transaction = new AsicRfTransaction(
            0,
            Convert.FromHexString("00300046000000907790770100F907"));
        var action = new AsicTimeGeneratorActionExecution(
            ProgramSelector: 0x40,
            SchedulePortAddress: 0x0881,
            ActionId: 52,
            Operand: 0,
            QuarterBit: 0,
            OccurrenceIndex: 1,
            OccurrenceCount: 2);

        Assert.Equal(0, source.ReadRawSample(0, transaction, action));
        Assert.Equal(0, source.DecodedSampleCount);
        Assert.Equal(0, source.MatchedSampleCount);
    }

    static void WriteTransaction(Cpu cpu, ReadOnlySpan<byte> bytes)
    {
        Assert.Equal(AsicRfFrontend.TransactionSize, bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            cpu.WriteData(AsicRfFrontend.FirstTransactionAddress + index, bytes[index]);
        }
    }
}
