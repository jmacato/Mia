// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class AsicTracePortTests
{
    [Fact]
    public void ReportsNoDebugPeerWhilePreservingOtherStatusBits()
    {
        var cpu = new Cpu(new byte[2], 0x1000);
        cpu.Data[AsicTracePort.StatusAddress] = 0x21;
        _ = new AsicTracePort(cpu);

        Assert.Equal(0xa1, cpu.ReadData(AsicTracePort.StatusAddress));
    }
}
