// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// Converts a host-camera snapshot to the 80x60 EBMP monitoring object
/// accepted by the native T68i Camera application. This models work owned by
/// the CommuniCam accessory, not work performed by the handset AVR.
/// </summary>
internal static class MiaCommuniCamMonitoringImageEncoder
{
    public const int HeaderLength = MiaCommuniCamEbmpEncoder.HeaderLength;

    public static byte[] Encode(MiaCameraFrame? frame) =>
        MiaCommuniCamEbmpEncoder.Encode(
            frame,
            MiaCommuniCamImageProfile.MonitoringWidth,
            MiaCommuniCamImageProfile.MonitoringHeight);
}
