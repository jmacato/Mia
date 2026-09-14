// SPDX-License-Identifier: MIT

using Mia.Desktop.Camera;
using Mia.Emulator.Camera;

namespace Mia.App.Tests;

internal sealed class RecordingCameraFrameSourceFactory(
    IMiaCameraFrameSource source) : IMiaCameraFrameSourceFactory
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public IMiaCameraFrameSource Create() => source;
}
