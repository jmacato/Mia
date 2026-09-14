// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal enum AsicChannelDecoderOperation : byte
{
    None,
    Sch,
    ControlChannel,
    TrafficFacch,
}
