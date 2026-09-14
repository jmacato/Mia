// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Camera;

internal interface IMiaCameraFrameSourceFactory
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    IMiaCameraFrameSource Create();
}
