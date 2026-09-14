// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace Mia.Desktop.Automation;

internal sealed record DesktopAutomationServerGsmCommandResponse(
    bool Accepted,
    string Result,
    MiaLiveGsmStatus Status);
