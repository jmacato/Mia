// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Automation;

internal sealed record DesktopAutomationServerIncomingSmsRequest(
    string? Originator,
    string? Text);
