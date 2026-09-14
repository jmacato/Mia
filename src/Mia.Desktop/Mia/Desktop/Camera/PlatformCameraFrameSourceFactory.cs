// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Camera;

/// <summary>
/// Selects a host camera provider without exposing platform details to the
/// emulator session. Additional operating-system backends can be added here.
/// </summary>
internal sealed class PlatformCameraFrameSourceFactory : IMiaCameraFrameSourceFactory
{
    public static PlatformCameraFrameSourceFactory Default { get; } = new();

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public string? UnavailableReason => IsAvailable
        ? null
        : "A host camera backend is not available for this operating system yet.";

    public IMiaCameraFrameSource Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacAvFoundationCameraFrameSource();
        }
        throw new PlatformNotSupportedException(UnavailableReason);
    }
}
