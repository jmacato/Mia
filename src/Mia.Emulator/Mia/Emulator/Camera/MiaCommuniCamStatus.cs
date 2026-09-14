// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal readonly record struct MiaCommuniCamStatus(
    MiaCommuniCamState State,
    string Message,
    long FramesSent)
{
    public static MiaCommuniCamStatus Disconnected { get; } = new(
        MiaCommuniCamState.Disconnected,
        "CommuniCam disconnected",
        0);
}
