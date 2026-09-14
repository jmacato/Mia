// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicFirmwareControllerTests
{
    [Fact]
    public void StartTransferCommandRaisesCompletionStatus()
    {
        var cpu = new Cpu(new byte[0x1000], 0x10000);
        _ = new AsicFirmwareController(cpu);

        cpu.WriteData(0x08e0, 0x23);

        Assert.Equal(0x01, cpu.ReadData(0x08e1));
    }

    [Fact]
    public void ModifiedStartTransferCommandRaisesCompletionStatus()
    {
        var cpu = new Cpu(new byte[0x1000], 0x10000);
        _ = new AsicFirmwareController(cpu);

        cpu.WriteData(0x08e0, 0xa3);

        Assert.Equal(0x01, cpu.ReadData(0x08e1));
    }

    [Fact]
    public void ResetCommandClearsCompletionStatus()
    {
        var cpu = new Cpu(new byte[0x1000], 0x10000);
        _ = new AsicFirmwareController(cpu);
        cpu.WriteData(0x08e0, 0x23);

        cpu.WriteData(0x08e0, 0x00);

        Assert.Equal(0x00, cpu.ReadData(0x08e1));
    }
}
