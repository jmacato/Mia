// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal sealed record GsmOutgoingNetworkRequest(
    Guid RequestId,
    GsmNetworkRequestKind Kind,
    string NormalizedDestination,
    string SmsText,
    bool International = false);
