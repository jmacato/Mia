// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicInterruptRouterTests
{
    [Fact]
    public void FirmwareRecordMapsPhysicalSourceToProcessDestination()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var controller = new AsicInterruptController(cpu);
        var router = new AsicInterruptRouter(cpu, controller);

        WriteRoute(cpu, physicalSource: 0x30, checkByte: 0xf9, processDestination: 0x09);

        Assert.True(router.TryResolveProcessDestination(0x30, out var processDestination));
        Assert.Equal(0x09, processDestination);
        var route = Assert.Single(router.Routes).Value;
        Assert.Equal(0x09, route.ProcessDestination);
        Assert.Equal(0xf9, route.CheckByte);
        Assert.Equal(0xff, route.Marker);
        Assert.Equal(0, route.Reserved);
    }

    [Fact]
    public void PhysicalInterruptRaisesSourceForFirmwareSelectedProcess()
    {
        var cpu = new Cpu(new byte[0x100], 0x1000)
        {
            PC = 0x12345,
            SP = 0x1000,
        };
        var controller = new AsicInterruptController(cpu);
        var router = new AsicInterruptRouter(cpu, controller);
        WriteRoute(cpu, physicalSource: 0x34, checkByte: 0xf8, processDestination: 0x0a);

        Assert.True(router.RaiseHighPriority(0x34));
        Assert.Equal(AsicInterruptController.TimeGeneratorAdcSource,
            cpu.Data[AsicInterruptController.SourceRegister]);
        Assert.Equal(1, controller.RaisedCount);
    }

    [Theory]
    [InlineData(0x2e, 0x0b, AsicInterruptController.PhDispatcherSource)]
    [InlineData(0x30, 0x09, AsicInterruptController.TimeGeneratorSc2Source)]
    [InlineData(0x32, 0x0c, AsicInterruptController.PhProcessSource)]
    [InlineData(0x34, 0x0a, AsicInterruptController.TimeGeneratorAdcSource)]
    public void LearnedPhysicalRoutesResolveThroughFixedVectorTable(
        byte physicalSource,
        byte processDestination,
        byte expectedInterruptSource)
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var router = new AsicInterruptRouter(cpu, new AsicInterruptController(cpu));
        WriteRoute(cpu, physicalSource, unchecked((byte)(2 - processDestination)),
            processDestination);

        Assert.True(router.TryResolveHighPrioritySource(physicalSource, out var interruptSource));
        Assert.Equal(expectedInterruptSource, interruptSource);
    }

    [Fact]
    public void RouteToUnprovenProcessFailsClosed()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var controller = new AsicInterruptController(cpu);
        var router = new AsicInterruptRouter(cpu, controller);
        WriteRoute(cpu, physicalSource: 0x40, checkByte: 0xf3, processDestination: 0x0f);

        Assert.True(router.TryResolveProcessDestination(0x40, out var processDestination));
        Assert.Equal(0x0f, processDestination);
        Assert.False(router.TryResolveHighPrioritySource(0x40, out _));
        Assert.False(router.RaiseHighPriority(0x40));
        Assert.Equal(0, controller.RaisedCount);
    }

    [Fact]
    public void StagingAndMalformedWritesDoNotCreateRoutes()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var router = new AsicInterruptRouter(cpu, new AsicInterruptController(cpu));

        cpu.WriteData(AsicInterruptRouter.PhysicalSourceAddress, 0);
        WriteRoute(cpu, physicalSource: 0x30, checkByte: 0, processDestination: 0x09);

        Assert.Empty(router.Routes);
        Assert.False(router.RaiseHighPriority(0x30));
    }

    static void WriteRoute(
        Cpu cpu,
        byte physicalSource,
        byte checkByte,
        byte processDestination)
    {
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress, checkByte);
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress + 1, processDestination);
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress + 2, 0xff);
        cpu.WriteData(AsicInterruptRouter.RouteDataAddress + 3, 0);
        cpu.WriteData(AsicInterruptRouter.PhysicalSourceAddress, physicalSource);
    }
}
