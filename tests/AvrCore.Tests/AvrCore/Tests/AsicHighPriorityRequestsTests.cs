// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicHighPriorityRequestsTests
{
    [Theory]
    [InlineData(AsicHighPriorityRequests.Sc0Request,
        AsicInterruptController.TimeGeneratorSc0Source)]
    [InlineData(AsicHighPriorityRequests.Sc1Request,
        AsicInterruptController.TimeGeneratorSc1Source)]
    [InlineData(AsicHighPriorityRequests.AdcRequest,
        AsicInterruptController.TimeGeneratorAdcSource)]
    public void RisingProvenBitRequestsItsFixedFirmwareHandler(
        byte request,
        byte expectedSource)
    {
        var cpu = CreateCpu();
        var interrupts = new AsicInterruptController(cpu);
        var requests = new AsicHighPriorityRequests(cpu, interrupts);

        cpu.WriteData(AsicHighPriorityRequests.RequestAddress, request);

        Assert.Equal(expectedSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(1, interrupts.RaisedCount);
        Assert.Equal(1, requests.RequestCount);
    }

    [Fact]
    public void RepeatedAndFallingWritesDoNotRequestAnotherHandler()
    {
        var cpu = CreateCpu();
        var interrupts = new AsicInterruptController(cpu);
        var requests = new AsicHighPriorityRequests(cpu, interrupts);

        cpu.WriteData(AsicHighPriorityRequests.RequestAddress,
            AsicHighPriorityRequests.Sc0Request);
        cpu.WriteData(AsicHighPriorityRequests.RequestAddress,
            AsicHighPriorityRequests.Sc0Request);
        cpu.WriteData(AsicHighPriorityRequests.RequestAddress, 0);

        Assert.Equal(1, interrupts.RaisedCount);
        Assert.Equal(1, requests.RequestCount);
    }

    [Fact]
    public void ANewRisingEdgeCanRequestTheHandlerAgain()
    {
        var cpu = CreateCpu();
        var interrupts = new AsicInterruptController(cpu);
        var requests = new AsicHighPriorityRequests(cpu, interrupts);

        cpu.WriteData(AsicHighPriorityRequests.RequestAddress,
            AsicHighPriorityRequests.Sc1Request);
        cpu.WriteData(AsicHighPriorityRequests.RequestAddress, 0);
        cpu.WriteData(AsicHighPriorityRequests.RequestAddress,
            AsicHighPriorityRequests.Sc1Request);

        Assert.Equal(2, interrupts.RaisedCount);
        Assert.Equal(2, requests.RequestCount);
    }

    [Fact]
    public void MaskedWriteUsesTheEffectiveRegisterValue()
    {
        var cpu = CreateCpu();
        var interrupts = new AsicInterruptController(cpu);
        var requests = new AsicHighPriorityRequests(cpu, interrupts);
        cpu.Data[AsicHighPriorityRequests.RequestAddress] = 0x80;

        cpu.WriteData(
            AsicHighPriorityRequests.RequestAddress,
            0x88,
            AsicHighPriorityRequests.Sc0Request);

        Assert.Equal(AsicInterruptController.TimeGeneratorSc0Source,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(1, requests.RequestCount);
    }

    [Fact]
    public void UnprovenBitsRemainInert()
    {
        var cpu = CreateCpu();
        var interrupts = new AsicInterruptController(cpu);
        var requests = new AsicHighPriorityRequests(cpu, interrupts);

        // Bits 0, 1, 2, 5, and 7 have no recovered CPU-write trigger
        // contract. Bit 5 is cleared by process 0x09, but its producer is
        // still an unknown hardware event rather than a proven STS request.
        cpu.WriteData(AsicHighPriorityRequests.RequestAddress, 0xa7);

        Assert.Equal(-1, cpu.NextInterrupt);
        Assert.Equal(0, interrupts.RaisedCount);
        Assert.Equal(0, requests.RequestCount);
    }

    [Fact]
    public void ExistingWriteHookStillReceivesTheMmioWrite()
    {
        var cpu = CreateCpu();
        var hookCalls = 0;
        cpu.WriteHooks[AsicHighPriorityRequests.RequestAddress] = (_, _, _, _) =>
        {
            hookCalls++;
            return false;
        };
        var requests = new AsicHighPriorityRequests(
            cpu,
            new AsicInterruptController(cpu));

        cpu.WriteData(AsicHighPriorityRequests.RequestAddress,
            AsicHighPriorityRequests.AdcRequest);

        Assert.Equal(1, hookCalls);
        Assert.Equal(1, requests.RequestCount);
    }

    static Cpu CreateCpu() => new(new byte[0x800000], 0x1000);
}
