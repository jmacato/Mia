// SPDX-License-Identifier: MIT

using Arm7Core;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemRomServicesTests
{
    [Fact]
    public void StringCopyUsesRecoveredTwoArgumentAbiAndCopiesTerminator()
    {
        var bus = new ArmModemRomServicesTestsTestBus();
        var cpu = new Arm7Tdmi(bus);
        var services = new ArmModemRomServices();
        bus.WriteBytes(0x20, [0x4c, 0x31, 0]);
        cpu.SetGpr(0, 0x40);
        cpu.SetGpr(1, 0x20);
        cpu.SetGpr(14, 0x101);

        bool handled = services.TryDispatch(cpu, bus, ArmModemRomServices.StringCopyAddress);

        Assert.True(handled);
        Assert.Equal(0x40u, cpu.GetGpr(0));
        Assert.Equal([0x4c, 0x31, 0], bus.ReadBytes(0x40, 3));
        Assert.Equal(0x20u, cpu.GetGpr(1));
        Assert.Equal(0x104u, cpu.GetGpr(15));
        Assert.Equal(1, services.CallCount);
    }

    [Fact]
    public void ByteCopyMatchesFirmwareFallbackRegisterResults()
    {
        var bus = new ArmModemRomServicesTestsTestBus();
        var cpu = new Arm7Tdmi(bus);
        var services = new ArmModemRomServices();
        bus.WriteBytes(0x20, [1, 2, 3]);
        cpu.SetGpr(0, 0x40);
        cpu.SetGpr(1, 0x20);
        cpu.SetGpr(2, 3);
        cpu.SetGpr(14, 0x101);

        Assert.True(services.TryDispatch(cpu, bus, ArmModemRomServices.ByteCopyAddress));

        Assert.Equal([1, 2, 3], bus.ReadBytes(0x40, 3));
        Assert.Equal(0x43u, cpu.GetGpr(0));
        Assert.Equal(0x23u, cpu.GetGpr(1));
        Assert.Equal(0u, cpu.GetGpr(2));
    }

    [Theory]
    [InlineData("wp_csett", "wp_csett", 0)]
    [InlineData("abc", "abd", -1)]
    [InlineData("abd", "abc", 1)]
    [InlineData("", "x", -120)]
    public void StringCompareUsesRecoveredTwoStringAbi(
        string left,
        string right,
        int expected)
    {
        var bus = new ArmModemRomServicesTestsTestBus();
        var cpu = new Arm7Tdmi(bus);
        var services = new ArmModemRomServices();
        bus.WriteBytes(0x20, [.. System.Text.Encoding.ASCII.GetBytes(left), 0]);
        bus.WriteBytes(0x60, [.. System.Text.Encoding.ASCII.GetBytes(right), 0]);
        cpu.SetGpr(0, 0x20);
        cpu.SetGpr(1, 0x60);
        cpu.SetGpr(14, 0x101);

        Assert.True(services.TryDispatch(
            cpu,
            bus,
            ArmModemRomServices.StringCompareAddress));

        Assert.Equal(unchecked((uint)expected), cpu.GetGpr(0));
        Assert.Equal(0x60u, cpu.GetGpr(1));
        Assert.Equal(0x104u, cpu.GetGpr(15));
        Assert.Equal(1, services.CallCount);
    }

    [Fact]
    public void StringCompareOrdersBytesAsUnsignedValues()
    {
        var bus = new ArmModemRomServicesTestsTestBus();
        var cpu = new Arm7Tdmi(bus);
        var services = new ArmModemRomServices();
        bus.WriteBytes(0x20, [0x80, 0]);
        bus.WriteBytes(0x60, [0x7f, 0]);
        cpu.SetGpr(0, 0x20);
        cpu.SetGpr(1, 0x60);
        cpu.SetGpr(14, 0x101);

        Assert.True(services.TryDispatch(
            cpu,
            bus,
            ArmModemRomServices.StringCompareAddress));

        Assert.Equal(1u, cpu.GetGpr(0));
    }
}
