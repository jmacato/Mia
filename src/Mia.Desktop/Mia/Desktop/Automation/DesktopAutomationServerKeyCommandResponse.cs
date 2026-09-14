// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace Mia.Desktop.Automation;

internal sealed record DesktopAutomationServerKeyCommandResponse(
    bool Accepted,
    MiaLiveGsmStatus Status);
