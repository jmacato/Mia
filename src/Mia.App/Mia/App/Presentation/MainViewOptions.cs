// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed record MainViewOptions(
    string Details,
    MainViewCapabilities Capabilities,
    bool AutoStart = true,
    bool PaceToRealTime = true);
