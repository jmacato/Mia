// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

internal sealed class AsicTracePort
{
    public const int StatusAddress = 0x0ac0;
    public const byte NoDebugPeer = 0x80;

    public AsicTracePort(Cpu cpu)
    {
        cpu.ReadHooks[StatusAddress] = address => (byte)(cpu.Data[address] | NoDebugPeer);
    }
}
