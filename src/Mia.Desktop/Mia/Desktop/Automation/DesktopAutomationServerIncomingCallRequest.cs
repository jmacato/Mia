// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Automation;

internal sealed record DesktopAutomationServerIncomingCallRequest(
    string? Originator,
    bool AutoAnswer = false);
