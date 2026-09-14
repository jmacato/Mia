// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

internal sealed class AsicHandsetPort
{
    public const int DataAddress = 0x0a40;
    public const int DirectionAddress = 0x0a41;
    public const byte HandsfreeDetectMask = 0x40;

    public AsicHandsetPort(Cpu cpu)
    {
        // The handsfree connector is unplugged. Its active-low detect input
        // is pulled high when configured as an input; output reads retain
        // the latch. The other pins share this port with the handset lights.
        cpu.ReadHooks[DataAddress] = address =>
            (byte)(cpu.Data[address] |
                (~cpu.Data[DirectionAddress] & HandsfreeDetectMask));
    }
}
