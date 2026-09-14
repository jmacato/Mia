// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal enum MiaCommuniCamState
{
    Disconnected,
    AccessoryHandshake,
    CableAttached,
    Multiplexing,
    ObexConnected,
    Streaming,
}
