// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicTimeGeneratorTests
{
    [Fact]
    public void ActionDefinitionBytesCommitToTheSelectedEntry()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);
        var observed = new List<(byte Selector, byte[] Bytes)>();
        timeGenerator.ActionDefinitionProgrammed +=
            (selector, definition) =>
                observed.Add((selector, definition.Bytes.ToArray()));

        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, 0xb5);
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, 0x0a);

        Assert.Empty(timeGenerator.ActionDefinitions);

        cpu.WriteData(AsicTimeGenerator.ActionDefinitionSelectorAddress, 0x01);

        Assert.Equal(
            new byte[] { 0xb5, 0x0a },
            timeGenerator.ActionDefinitions[0x01].Bytes.ToArray());
        Assert.Equal(1, timeGenerator.ProgrammedActionDefinitionCount);
        Assert.Single(observed);
        Assert.Equal((byte)0x01, observed[0].Selector);
        Assert.Equal(new byte[] { 0xb5, 0x0a }, observed[0].Bytes);
    }

    [Fact]
    public void ActionDefinitionCommitStartsTheNextVariableLengthEntry()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, 0x11);
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionSelectorAddress, 0x59);
        foreach (var value in new byte[] { 0xbc, 0x11, 0x28, 0x12 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, value);
        }
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionSelectorAddress, 0x75);

        Assert.Equal(
            new byte[] { 0x11 },
            timeGenerator.ActionDefinitions[0x59].Bytes.ToArray());
        Assert.Equal(
            new byte[] { 0xbc, 0x11, 0x28, 0x12 },
            timeGenerator.ActionDefinitions[0x75].Bytes.ToArray());
        Assert.Equal(2, timeGenerator.ProgrammedActionDefinitionCount);
    }

    [Fact]
    public void ActionBytesCommitToTheSelectedProgram()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);
        var observed = new List<(byte Selector, byte[] Bytes)>();
        timeGenerator.ActionProgrammed +=
            (selector, program) => observed.Add((selector, program.Bytes.ToArray()));

        foreach (var value in new byte[] { 0x00, 0x88, 0x76, 0xa4, 0x58, 0xda })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }

        Assert.Empty(timeGenerator.ActionPrograms);

        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x02);

        var expected = new byte[] { 0x00, 0x88, 0x76, 0xa4, 0x58, 0xda };
        Assert.Equal(expected, timeGenerator.ActionPrograms[0x02].Bytes.ToArray());
        Assert.Single(observed);
        Assert.Equal((byte)0x02, observed[0x00].Selector);
        Assert.Equal(expected, observed[0x00].Bytes);
    }

    [Fact]
    public void ActionCommitStartsTheNextProgram()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x11);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x22);
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x23);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x01);

        Assert.Equal(
            new byte[] { 0x11 },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
        Assert.Equal(
            new byte[] { 0x22, 0x23 },
            timeGenerator.ActionPrograms[0x01].Bytes.ToArray());
    }

    [Fact]
    public void StandaloneAdvanceStrobesDoNotJoinTheActionProgram()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramAdvance);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramAdvance);
        foreach (var value in new byte[] { 0x00, 0xa4, 0x58, 0xda, 0x88, 0x76 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);

        Assert.Equal(
            new byte[] { 0x00, 0xa4, 0x58, 0xda, 0x88, 0x76 },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
        Assert.Equal(2, timeGenerator.ActionProgramAdvanceCount);
        Assert.Equal(
            (byte)0x76,
            cpu.ReadData(AsicTimeGenerator.ActionProgramAddress));
    }

    [Fact]
    public void AdvanceFrameControlPairDoesNotJoinAStagedActionProgram()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        foreach (var value in new byte[] { 0x00, 0xa4, 0x58, 0xda, 0x88, 0x76 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramAdvance);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramFrameControl);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);

        Assert.Equal(
            new byte[] { 0x00, 0xa4, 0x58, 0xda, 0x88, 0x76 },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
        Assert.Equal(1, timeGenerator.ActionProgramAdvanceCount);
    }

    [Fact]
    public void ExtendedAdvanceSequenceDoesNotJoinAStagedActionProgram()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        foreach (var value in new byte[] { 0x00, 0xa4, 0x58, 0xda, 0x88, 0x76 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        foreach (var value in new byte[]
                 {
                     AsicTimeGenerator.ActionProgramAdvance,
                     0x05, 0x07, 0x09,
                     AsicTimeGenerator.ActionProgramFrameControl,
                     0x8b, 0x8d, 0x8f,
                 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);

        Assert.Equal(
            new byte[] { 0x00, 0xa4, 0x58, 0xda, 0x88, 0x76 },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
        Assert.Equal(1, timeGenerator.ActionProgramAdvanceCount);
    }

    [Fact]
    public void DedicatedAdvancePrefixDoesNotJoinAStagedActionProgram()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        foreach (var value in new byte[] { 0x08, 0xac, 0x60, 0xe2, 0x88, 0x78 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        foreach (var value in new byte[]
                 {
                     AsicTimeGenerator.ActionProgramAdvancePrefix,
                     AsicTimeGenerator.ActionProgramAdvance,
                     0x05, 0x89, 0x8b, 0x59, 0xdb, 0x93,
                 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x04);

        Assert.Equal(
            new byte[] { 0x08, 0xac, 0x60, 0xe2, 0x88, 0x78 },
            timeGenerator.ActionPrograms[0x04].Bytes.ToArray());
        Assert.Equal(1, timeGenerator.ActionProgramAdvanceCount);
    }

    [Fact]
    public void NewProgramAfterUncommittedAdvanceDiscardsAbandonedBytes()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        foreach (var value in new byte[] { 0x08, 0xac, 0x60, 0xe2, 0x88, 0x78 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        foreach (var value in new byte[]
                 {
                     AsicTimeGenerator.ActionProgramAdvancePrefix,
                     AsicTimeGenerator.ActionProgramAdvance,
                     0x05, 0x89,
                     0x00, 0x88, 0x76, 0xa4, 0x58, 0xda,
                 })
        {
            cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, value);
        }
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);

        Assert.Equal(
            new byte[] { 0x00, 0x88, 0x76, 0xa4, 0x58, 0xda },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
    }

    [Fact]
    public void UnpairedAdvancePrefixRemainsAnActionProgramByte()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramAdvancePrefix);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);

        Assert.Equal(
            new byte[] { AsicTimeGenerator.ActionProgramAdvancePrefix },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
    }

    [Fact]
    public void UnpairedFrameControlValueRemainsAnActionProgramByte()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x89);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);

        Assert.Equal(
            new byte[] { 0x89 },
            timeGenerator.ActionPrograms[0x00].Bytes.ToArray());
    }

    [Fact]
    public void ThreadedGeneratorPreservesActionDescriptorOrder()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        using var worker = new MiaWorker("test time-generator owner");
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, new AsicInterruptController(cpu)),
            worker);
        var observed = new List<string>();
        timeGenerator.ActionProgrammed +=
            (selector, _) => observed.Add($"action:{selector}");
        timeGenerator.ActionDefinitionProgrammed +=
            (selector, _) => observed.Add($"definition:{selector}");
        timeGenerator.DescriptorProgrammed +=
            (address, _) => observed.Add($"descriptor:{address:x4}");

        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, 0xb5);
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, 0x0a);
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionSelectorAddress, 0x01);
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x44);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x03);
        cpu.WriteData(0x0884, 0x4d);
        cpu.WriteData(0x0884, 0x81);
        cpu.WriteData(0x0884, 0xdf);

        timeGenerator.Synchronize();

        Assert.Equal(
            ["definition:1", "action:3", "descriptor:0884"],
            observed);
    }

    [Fact]
    public void ThreadedTransactionsReachTheirOwnerOnTheNextAsicCycle()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        using var worker = new MiaWorker("test time-generator owner");
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, new AsicInterruptController(cpu)),
            worker);

        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x44);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x03);
        Assert.Empty(timeGenerator.ActionPrograms);

        clock.AdvanceBy(1);

        Assert.Equal(
            new byte[] { 0x44 },
            timeGenerator.ActionPrograms[0x03].Bytes.ToArray());
    }

    [Fact]
    public void ThreeWritesProgramOneOpaqueDescriptor()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);
        var observed = new List<(int Address, AsicTimeGeneratorDescriptor Descriptor)>();
        timeGenerator.DescriptorProgrammed +=
            (address, descriptor) => observed.Add((address, descriptor));

        cpu.WriteData(0x0881, 0x4d);
        cpu.WriteData(0x0881, 0x81);

        Assert.False(timeGenerator.TryGetDescriptor(0x0881, out _));
        Assert.Equal(0, timeGenerator.ProgrammedDescriptorCount);

        cpu.WriteData(0x0881, 0xdf);

        var expected = new AsicTimeGeneratorDescriptor(0x4d, 0x81, 0xdf);
        Assert.True(timeGenerator.TryGetDescriptor(0x0881, out var descriptor));
        Assert.Equal(expected, descriptor);
        Assert.Equal(expected, timeGenerator.Descriptors[0x0881]);
        Assert.Equal([(0x0881, expected)], observed);
        Assert.Equal(1, timeGenerator.ProgrammedDescriptorCount);
    }

    [Fact]
    public void SchedulePortsHaveIndependentThreeByteLatches()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(0x0881, 0x11);
        cpu.WriteData(0x0882, 0x21);
        cpu.WriteData(0x0881, 0x12);
        cpu.WriteData(0x0882, 0x22);
        cpu.WriteData(0x0882, 0x23);

        Assert.False(timeGenerator.TryGetDescriptor(0x0881, out _));
        Assert.True(timeGenerator.TryGetDescriptor(0x0882, out var descriptor));
        Assert.Equal(new AsicTimeGeneratorDescriptor(0x21, 0x22, 0x23), descriptor);
        Assert.Equal(1, timeGenerator.ProgrammedDescriptorCount);

        cpu.WriteData(0x0881, 0x13);

        Assert.True(timeGenerator.TryGetDescriptor(0x0881, out descriptor));
        Assert.Equal(new AsicTimeGeneratorDescriptor(0x11, 0x12, 0x13), descriptor);
        Assert.Equal(2, timeGenerator.ProgrammedDescriptorCount);
    }

    [Fact]
    public void ASubsequentTripletReplacesTheLatestDescriptor()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        foreach (var value in new byte[] { 1, 2, 3, 4, 5, 6 })
        {
            cpu.WriteData(AsicTimeGenerator.FirstSchedulePortAddress, value);
        }

        Assert.True(timeGenerator.TryGetDescriptor(
            AsicTimeGenerator.FirstSchedulePortAddress,
            out var descriptor));
        Assert.Equal(new AsicTimeGeneratorDescriptor(4, 5, 6), descriptor);
        Assert.Equal(2, timeGenerator.ProgrammedDescriptorCount);
    }

    [Fact]
    public void ThreadedGeneratorReceivesOneOwnedTransactionPerDescriptor()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        using var worker = new MiaWorker("test time-generator owner");
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, new AsicInterruptController(cpu)),
            worker);

        cpu.WriteData(0x0881, 0x4d);
        cpu.WriteData(0x0881, 0x81);

        Assert.Equal(0, worker.CompletedWorkCount);
        Assert.False(timeGenerator.TryGetDescriptor(0x0881, out _));

        cpu.WriteData(0x0881, 0xdf);

        timeGenerator.Synchronize();
        Assert.Equal(1, worker.CompletedWorkCount);
        Assert.True(timeGenerator.TryGetDescriptor(0x0881, out var descriptor));
        Assert.Equal(
            new AsicTimeGeneratorDescriptor(0x4d, 0x81, 0xdf),
            descriptor);
    }

    [Fact]
    public void DescriptorSchedulesDefinedActionsAtAbsoluteQuarterBits()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);
        var observed = new List<(long Cycle, AsicTimeGeneratorActionExecution Action)>();
        timeGenerator.ActionExecuted += action => observed.Add((clock.Cycles, action));

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        WriteDefinition(cpu, 54, 10, 20);
        WriteActionProgram(cpu, 0x00, (657 << 6) | 54);
        WriteDescriptor(cpu, 0x0881);

        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 10 - 1);
        Assert.Empty(observed);
        clock.AdvanceBy(1);

        var first = Assert.Single(observed);
        Assert.Equal(AsicTimeGenerator.FrameRolloverCycles + 120, first.Cycle);
        Assert.Equal((byte)54, first.Action.ActionId);
        Assert.Equal((ushort)657, first.Action.Operand);
        Assert.Equal((ushort)10, first.Action.QuarterBit);
        Assert.Equal(0, first.Action.OccurrenceIndex);
        Assert.Equal(2, first.Action.OccurrenceCount);

        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 10);

        Assert.Equal(2, observed.Count);
        Assert.Equal(AsicTimeGenerator.FrameRolloverCycles + 240, observed[1].Cycle);
        Assert.Equal(1, observed[1].Action.OccurrenceIndex);
        Assert.Equal(2, timeGenerator.ExecutedActionCount);
    }

    [Fact]
    public void TrailingScheduledEncoderTokenUsesItsDirectDefinition()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);
        var observed = new List<(long Cycle, AsicTimeGeneratorActionExecution Action)>();
        timeGenerator.ActionExecuted += action => observed.Add((clock.Cycles, action));

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        const ushort quarterBit = 10;
        cpu.WriteData(AsicTimeGenerator.ActionDefinitionDataAddress, (byte)quarterBit);
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionDataAddress,
            (byte)(quarterBit >> 8));
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionSelectorAddress,
            AsicTimeGenerator.ScheduledEncoderActionToken);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ScheduledEncoderActionToken);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);
        WriteDescriptor(cpu, 0x0881);

        clock.AdvanceBy(quarterBit * AsicTimeGenerator.QuarterBitCycles);

        var execution = Assert.Single(observed).Action;
        Assert.Equal(AsicTimeGenerator.ScheduledEncoderActionId, execution.ActionId);
        Assert.Equal((ushort)0, execution.Operand);
        Assert.Equal(quarterBit, execution.QuarterBit);
    }

    [Fact]
    public void TrailingScheduledDecoderTokenSurvivesAdvancePrefixAndReusesTimeslot()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);
        var observed = new List<AsicTimeGeneratorActionExecution>();
        timeGenerator.ActionExecuted += observed.Add;

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        const ushort quarterBit = 10;
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionDataAddress,
            (byte)quarterBit);
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionDataAddress,
            (byte)(quarterBit >> 8));
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionSelectorAddress,
            AsicTimeGenerator.ScheduledEncoderActionToken);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ScheduledDecoderActionToken);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramAdvancePrefix);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramAdvance);
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, 0x05);
        cpu.WriteData(
            AsicTimeGenerator.ActionProgramAddress,
            AsicTimeGenerator.ActionProgramFrameControl);
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, 0x00);
        WriteDescriptor(cpu, 0x0881);

        clock.AdvanceBy(quarterBit * AsicTimeGenerator.QuarterBitCycles);

        Assert.Equal(
            new byte[] { AsicTimeGenerator.ScheduledDecoderActionToken },
            timeGenerator.ActionPrograms[0].Bytes.ToArray());
        var execution = Assert.Single(observed);
        Assert.Equal(AsicTimeGenerator.ScheduledDecoderActionId, execution.ActionId);
        Assert.Equal((ushort)0, execution.Operand);
        Assert.Equal(quarterBit, execution.QuarterBit);
    }

    [Fact]
    public void CommittedDescriptorIsOneShotUntilFirmwareReprogramsIt()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);
        var observed = new List<AsicTimeGeneratorActionExecution>();
        timeGenerator.ActionExecuted += observed.Add;

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        WriteDefinition(cpu, 52, 100);
        WriteActionProgram(cpu, 0x01, (665 << 6) | 52);
        WriteDescriptor(cpu, 0x0882);
        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 100);
        Assert.Single(observed);

        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);

        Assert.Single(observed);
    }

    [Fact]
    public void ReprogrammingAPortSuppressesItsOlderPendingActions()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);
        var observed = new List<AsicTimeGeneratorActionExecution>();
        timeGenerator.ActionExecuted += observed.Add;

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);
        WriteDefinition(cpu, 54, 100);
        WriteActionProgram(cpu, 0x00, (657 << 6) | 54);
        WriteDescriptor(cpu, 0x0881);
        WriteDescriptor(cpu, 0x0881);

        clock.AdvanceBy(AsicTimeGenerator.QuarterBitCycles * 100);

        Assert.Single(observed);
    }

    [Theory]
    [InlineData(AsicTimeGenerator.ScheduleCommit)]
    [InlineData(AsicTimeGenerator.ConfigurationCommit)]
    [InlineData(AsicTimeGenerator.CommandMask)]
    public void CommandBitsRemainBusyUntilTheInternalCommitCompletes(byte command)
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var clock = new MiaSystemClock();
        var timeGenerator = CreateTimeGenerator(cpu, clock);

        cpu.WriteData(AsicTimeGenerator.CommandStatusAddress, command);

        Assert.Equal(command, cpu.ReadData(AsicTimeGenerator.CommandStatusAddress));
        Assert.Equal(command == AsicTimeGenerator.CommandMask ? 2 : 1,
            timeGenerator.StartedCommands);
        Assert.Equal(0, timeGenerator.CompletedCommands);

        clock.AdvanceBy(AsicTimeGenerator.CommandCompletionCycles);

        Assert.Equal(0, cpu.ReadData(AsicTimeGenerator.CommandStatusAddress));
        Assert.Equal(timeGenerator.StartedCommands, timeGenerator.CompletedCommands);
    }

    [Fact]
    public void NonCommandStatusBitsAreReadOnly()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(AsicTimeGenerator.CommandStatusAddress, 0x6f);

        Assert.Equal(0, cpu.ReadData(AsicTimeGenerator.CommandStatusAddress));
        Assert.Equal(0, timeGenerator.StartedCommands);
    }

    [Fact]
    public void MaskedWriteStartsOnlySelectedCommandBits()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var timeGenerator = CreateTimeGenerator(cpu);

        cpu.WriteData(
            AsicTimeGenerator.CommandStatusAddress,
            AsicTimeGenerator.CommandMask,
            AsicTimeGenerator.ConfigurationCommit);

        Assert.Equal(AsicTimeGenerator.ConfigurationCommit,
            cpu.ReadData(AsicTimeGenerator.CommandStatusAddress));
        Assert.Equal(1, timeGenerator.StartedCommands);
    }

    [Fact]
    public void FirmwareControlPatternStartsRoutedFrameRolloverPair()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var controller = new AsicInterruptController(cpu);
        var router = new AsicInterruptRouter(cpu, controller);
        var clock = new MiaSystemClock();
        WriteRoute(cpu, AsicTimeGenerator.PhProcessPhysicalSource, 0x0c);
        WriteRoute(cpu, AsicTimeGenerator.PhDispatcherPhysicalSource, 0x0b);
        var timeGenerator = new AsicTimeGenerator(cpu, clock, router);

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0x01);
        Assert.False(timeGenerator.FrameRolloverEnabled);

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        Assert.True(timeGenerator.FrameRolloverEnabled);

        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles - 1);
        Assert.Equal(0, timeGenerator.FrameRolloverCount);
        Assert.Equal(0, controller.RaisedCount);

        clock.AdvanceBy(1);

        Assert.Equal(1, timeGenerator.FrameRolloverCount);
        Assert.Equal(2, controller.RaisedCount);
        Assert.Equal(AsicInterruptController.PhProcessSource,
            cpu.Data[AsicInterruptController.SourceRegister]);

        cpu.Data[0x5f] = 0x80;
        cpu.Tick();
        controller.NotifyHighPriorityDispatched();
        controller.NotifyHighPriorityReturned();
        Assert.Equal(AsicInterruptController.PhDispatcherSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
    }

    [Fact]
    public void FramePairCrossesEachOwningWorkerOnce()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        using var interruptWorker = new MiaWorker("test interrupt owner");
        using var routerWorker = new MiaWorker("test router owner");
        using var generatorWorker = new MiaWorker("test generator owner");
        var controller = new AsicInterruptController(cpu, interruptWorker);
        var router = new AsicInterruptRouter(cpu, controller, routerWorker);
        var clock = new MiaSystemClock();
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            router,
            generatorWorker);
        WriteRoute(cpu, AsicTimeGenerator.PhProcessPhysicalSource, 0x0c);
        WriteRoute(cpu, AsicTimeGenerator.PhDispatcherPhysicalSource, 0x0b);
        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        var interruptWork = interruptWorker.CompletedWorkCount;
        var routerWork = routerWorker.CompletedWorkCount;
        var generatorWork = generatorWorker.CompletedWorkCount;

        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);

        Assert.Equal(generatorWork + 1, generatorWorker.CompletedWorkCount);
        Assert.Equal(routerWork, routerWorker.CompletedWorkCount);
        Assert.Equal(interruptWork, interruptWorker.CompletedWorkCount);
        Assert.Equal(2, controller.RaisedCount);
    }

    [Fact]
    public void ClearingControlPatternStopsAndReenableStartsFreshPeriod()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000);
        var controller = new AsicInterruptController(cpu);
        var clock = new MiaSystemClock();
        var timeGenerator = new AsicTimeGenerator(
            cpu,
            clock,
            new AsicInterruptRouter(cpu, controller));

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles / 2);
        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0x01);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);

        Assert.False(timeGenerator.FrameRolloverEnabled);
        Assert.Equal(0, timeGenerator.FrameRolloverCount);

        cpu.WriteData(AsicTimeGenerator.FrameControlAddress, 0xe1);
        clock.AdvanceBy(AsicTimeGenerator.FrameRolloverCycles);

        Assert.True(timeGenerator.FrameRolloverEnabled);
        Assert.Equal(1, timeGenerator.FrameRolloverCount);
        Assert.Equal(0, controller.RaisedCount);
    }

    static AsicTimeGenerator CreateTimeGenerator(
        Cpu cpu,
        MiaSystemClock? clock = null) =>
        new(
            cpu,
            clock ?? new MiaSystemClock(),
            new AsicInterruptRouter(cpu, new AsicInterruptController(cpu)));

    static void WriteDefinition(Cpu cpu, byte actionId, params ushort[] quarterBits)
    {
        foreach (var quarterBit in quarterBits)
        {
            cpu.WriteData(
                AsicTimeGenerator.ActionDefinitionDataAddress,
                (byte)quarterBit);
            cpu.WriteData(
                AsicTimeGenerator.ActionDefinitionDataAddress,
                (byte)(quarterBit >> 8));
        }
        cpu.WriteData(
            AsicTimeGenerator.ActionDefinitionSelectorAddress,
            unchecked((byte)(actionId + 1)));
    }

    static void WriteActionProgram(Cpu cpu, byte selector, int word)
    {
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, (byte)word);
        cpu.WriteData(AsicTimeGenerator.ActionProgramAddress, (byte)(word >> 8));
        cpu.WriteData(AsicTimeGenerator.ActionSelectorAddress, selector);
    }

    static void WriteDescriptor(Cpu cpu, int address)
    {
        cpu.WriteData(address, 0x4d);
        cpu.WriteData(address, 0x81);
        cpu.WriteData(address, 0xdf);
    }

    static void WriteRoute(Cpu cpu, byte physicalSource, byte processDestination)
    {
        cpu.WriteData(
            AsicInterruptRouter.RouteDataAddress,
            unchecked((byte)(2 - processDestination)));
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress + 1, processDestination);
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress + 2, 0xff);
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress + 3, 0);
        cpu.WriteData(AsicInterruptRouter.PhysicalSourceAddress, physicalSource);
    }
}
