// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicTransferControllerTests
{
    [Fact]
    public void ModeZeroSelectorZeroCopiesIdentityAndSetsCompletionBit()
    {
        var (cpu, clock, controller) = CreateController();
        var source = Enumerable.Range(0, 39).Select(value => (byte)value).ToArray();
        source.CopyTo(cpu.Data, AsicTransferController.DataBankBase + 0x1860);

        SubmitModeZero(cpu, new(0x1860, 0x069d, 39), selector: 0);

        Assert.Equal(0, cpu.ReadData(AsicTransferController.ControlAddress) &
            AsicTransferController.CompleteMask);
        clock.AdvanceBy(AsicTransferController.CompletionCycles);

        Assert.Equal(source, cpu.Data.AsSpan(
            AsicTransferController.DataBankBase + 0x069d,
            source.Length).ToArray());
        Assert.Equal(
            AsicTransferController.CompleteMask,
            cpu.ReadData(AsicTransferController.ControlAddress));
        Assert.Equal(1, controller.StartedCount);
        Assert.Equal(1, controller.CompletedCount);
        Assert.Equal(0, controller.RejectedCount);
    }

    [Fact]
    public void ModeZeroUsesDocumentedForwardCircularByteSelector()
    {
        var (cpu, clock, controller) = CreateController();
        const int length = 7;
        byte[] source = [0, 1, 2, 3, 4, 5, 6];
        source.CopyTo(cpu.Data, AsicTransferController.DataBankBase + 0x1200);

        SubmitModeZero(cpu, new(0x1200, 0x1300, length), selector: 3);
        clock.AdvanceBy(AsicTransferController.CompletionCycles);

        Assert.Equal(
            new byte[] { 3, 4, 5, 6, 0, 1, 2 },
            cpu.Data.AsSpan(
                AsicTransferController.DataBankBase + 0x1300,
                length).ToArray());
        Assert.Equal(3, controller.LastSelector);
    }

    [Fact]
    public void UnknownDescriptorPairFailsClosedWithoutCompleting()
    {
        var (cpu, clock, controller) = CreateController();
        StageDescriptor(cpu, 0x1200, 7, control: 0x20);
        StageDescriptor(cpu, 0x1300, 7, control: 0x01);
        ResetParametersAndStart(cpu, selector: 0);

        clock.AdvanceBy(AsicTransferController.CompletionCycles);

        Assert.Equal(0, controller.StartedCount);
        Assert.Equal(0, controller.CompletedCount);
        Assert.Equal(1, controller.RejectedCount);
        Assert.Equal(0, cpu.ReadData(AsicTransferController.ControlAddress) &
            AsicTransferController.CompleteMask);
    }

    [Fact]
    public void ModeTwentyDeinterleavesStrideTwoLaneIntoControlBlock()
    {
        var (cpu, clock, controller) = CreateController();
        var half = Enumerable.Range(0, AsicTransferController.NormalBurstHalfLength)
            .Select(value => (byte)value)
            .ToArray();
        half.CopyTo(
            cpu.Data,
            AsicTransferController.DataBankBase +
                AsicTransferController.Phase2UpperHalfAddress);
        cpu.Data.AsSpan(
            AsicTransferController.DataBankBase + 0x18f9,
            AsicTransferController.ControlChannelBlockLength).Fill(0xff);

        SubmitModeTwenty(
            cpu,
            new(AsicTransferController.Phase2UpperHalfAddress, 0x18f9, 29),
            destinationStart: 196,
            selector: 0);
        clock.AdvanceBy(AsicTransferController.CompletionCycles);

        var destination = cpu.Data.AsSpan(
            AsicTransferController.DataBankBase + 0x18f9,
            AsicTransferController.ControlChannelBlockLength);
        for (var index = 0; index < 29; index++)
        {
            Assert.Equal(
                half[2 * index % AsicTransferController.NormalBurstHalfLength],
                destination[(196 + 64 * index) %
                    AsicTransferController.ControlChannelBlockLength]);
        }
        Assert.Equal(29, destination.ToArray().Count(value => value != 0xff));
        Assert.Equal(1, controller.CompletedCount);
    }

    [Fact]
    public void ModeTwentyAppliesSelectorBeforeStrideExtraction()
    {
        var (cpu, clock, _) = CreateController();
        var half = Enumerable.Range(0, AsicTransferController.NormalBurstHalfLength)
            .Select(value => (byte)value)
            .ToArray();
        half.CopyTo(
            cpu.Data,
            AsicTransferController.DataBankBase +
                AsicTransferController.Phase2LowerHalfAddress);

        SubmitModeTwenty(
            cpu,
            new(AsicTransferController.Phase2LowerHalfAddress + 1, 0x18f9, 28),
            destinationStart: 32,
            selector: 3);
        clock.AdvanceBy(AsicTransferController.CompletionCycles);

        var destination = cpu.Data.AsSpan(
            AsicTransferController.DataBankBase + 0x18f9,
            AsicTransferController.ControlChannelBlockLength);
        for (var index = 0; index < 28; index++)
        {
            Assert.Equal(
                half[(1 + 2 * index + 3) %
                    AsicTransferController.NormalBurstHalfLength],
                destination[(32 + 64 * index) %
                    AsicTransferController.ControlChannelBlockLength]);
        }
    }

    [Fact]
    public void MachineWiresTransferController()
    {
        using var machine = new MiaMachine(new byte[0x100]);
        var source = Enumerable.Range(0, 39).Select(value => (byte)value).ToArray();
        source.CopyTo(
            machine.Cpu.Data,
            AsicTransferController.DataBankBase + 0x1839);

        SubmitModeZero(machine.Cpu, new(0x1839, 0x0676, 39), selector: 0);
        machine.Clock.AdvanceBy(AsicTransferController.CompletionCycles);

        Assert.Equal(1, machine.TransferController.CompletedCount);
        Assert.Equal(source, machine.Cpu.Data.AsSpan(
            AsicTransferController.DataBankBase + 0x0676,
            source.Length).ToArray());
    }

    static (Cpu Cpu, MiaSystemClock Clock, AsicTransferController Controller)
        CreateController()
    {
        var cpu = new Cpu(new byte[0x100], 0x110000);
        var clock = new MiaSystemClock();
        return (cpu, clock, new(cpu, clock));
    }

    static void SubmitModeZero(
        Cpu cpu,
        TransferDescriptor transfer,
        byte selector)
    {
        StageDescriptor(
            cpu,
            transfer.Source,
            transfer.Length,
            AsicTransferController.LinearSourceControl);
        StageDescriptor(
            cpu,
            transfer.Destination,
            transfer.Length,
            AsicTransferController.CircularDestinationControl);
        ResetParametersAndStart(cpu, selector);
    }

    static void SubmitModeTwenty(
        Cpu cpu,
        TransferDescriptor transfer,
        ushort destinationStart,
        byte selector)
    {
        StageDescriptor(
            cpu,
            transfer.Source,
            transfer.Length,
            AsicTransferController.StrideTwoSourceControl);
        StageDescriptor(
            cpu,
            transfer.Destination,
            transfer.Length,
            AsicTransferController.LinearDestinationControl);
        cpu.WriteData(AsicTransferController.ControlAddress, 0);
        WriteParameter(cpu, 0, transfer.Destination);
        WriteParameter(cpu, 1, destinationStart);
        WriteParameter(cpu, 2, AsicTransferController.ControlChannelMode);
        cpu.WriteData(AsicTransferController.SelectorAddress, selector);
        cpu.WriteData(
            AsicTransferController.ControlAddress,
            AsicTransferController.StartControl);
    }

    static void StageDescriptor(
        Cpu cpu,
        ushort address,
        ushort length,
        byte control)
    {
        cpu.WriteData(AsicTransferController.DescriptorControl, 0);
        cpu.WriteData(AsicTransferController.DescriptorAddressLow, (byte)address);
        cpu.WriteData(
            AsicTransferController.DescriptorAddressLow + 1,
            (byte)(address >> 8));
        cpu.WriteData(AsicTransferController.DescriptorLengthLow, (byte)length);
        cpu.WriteData(
            AsicTransferController.DescriptorLengthLow + 1,
            (byte)(length >> 8));
        cpu.WriteData(AsicTransferController.DescriptorControl, control);
    }

    static void ResetParametersAndStart(Cpu cpu, byte selector)
    {
        cpu.WriteData(AsicTransferController.ControlAddress, 0);
        cpu.WriteData(AsicTransferController.FirstParameterAddress, 0);
        cpu.WriteData(AsicTransferController.FirstParameterAddress, 0);
        cpu.WriteData(AsicTransferController.LastParameterAddress, 0);
        cpu.WriteData(AsicTransferController.LastParameterAddress, 0);
        cpu.WriteData(AsicTransferController.SelectorAddress, selector);
        cpu.WriteData(
            AsicTransferController.ControlAddress,
            AsicTransferController.StartControl);
    }


    static void WriteParameter(Cpu cpu, int parameter, ushort value)
    {
        var address = AsicTransferController.FirstParameterAddress + parameter;
        cpu.WriteData(address, (byte)value);
        cpu.WriteData(address, (byte)(value >> 8));
    }
}
