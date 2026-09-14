// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

[Flags]
public enum MainViewCapabilities
{
    None = 0,
    Diagnostics = 1 << 0,
    LiveAudio = 1 << 2,
    ReloadForRestart = 1 << 3,
    CommuniCam = 1 << 4,
}
