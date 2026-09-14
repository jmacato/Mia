// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// Image modes exposed by the CommuniCam accessory. Host-camera backends
/// provide a sensor-sized source frame; protocol encoders derive the preview
/// and, later, thumbnail and still-image objects from that source.
/// </summary>
internal static class MiaCommuniCamImageProfile
{
    public const int SensorWidth = 640;
    public const int SensorHeight = 480;
    public const int MonitoringWidth = 80;
    public const int MonitoringHeight = 60;
    public const int ThumbnailWidth = 101;
    public const int ThumbnailHeight = 80;
    public const int MonitoringFramesPerSecond = 1;

    public static bool IsSupportedNativeSize(int width, int height) =>
        (width, height) is
            (80, 60) or
            (160, 120) or
            (320, 240) or
            (640, 480);

    public static bool TryParseNativeSize(
        string? value,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int separator = value.IndexOf('*', StringComparison.Ordinal);
        return separator > 0 && separator < value.Length - 1 &&
            int.TryParse(
                value.AsSpan(0, separator),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out width) &&
            int.TryParse(
                value.AsSpan(separator + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out height) &&
            IsSupportedNativeSize(width, height);
    }
}
