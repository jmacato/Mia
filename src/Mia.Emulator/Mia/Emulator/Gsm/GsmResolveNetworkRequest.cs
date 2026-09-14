// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal sealed record GsmResolveNetworkRequest(
    Guid RequestId,
    GsmNetworkRequestDecision Decision);
