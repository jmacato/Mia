// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly record struct GsmPendingOutgoingNetworkRequest(
    GsmOutgoingNetworkRequest Request,
    byte TransactionAndProtocolDiscriminator,
    byte MessageReference,
    byte Sapi);
