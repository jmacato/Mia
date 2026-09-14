// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using Mia.Emulator;

namespace Mia.Desktop.Automation;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(MiaLiveGsmStatus))]
[JsonSerializable(typeof(DesktopAutomationServerIncomingSmsRequest))]
[JsonSerializable(typeof(DesktopAutomationServerIncomingCallRequest))]
[JsonSerializable(typeof(DesktopAutomationServerRssiRequest))]
[JsonSerializable(typeof(DesktopAutomationServerErrorResponse))]
[JsonSerializable(typeof(DesktopAutomationServerGsmCommandResponse))]
[JsonSerializable(typeof(DesktopAutomationServerKeyCommandResponse))]
internal sealed partial class DesktopAutomationServerJsonContext : JsonSerializerContext;
