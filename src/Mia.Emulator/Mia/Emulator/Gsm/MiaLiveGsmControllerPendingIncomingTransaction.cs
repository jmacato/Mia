// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly record struct MiaLiveGsmControllerPendingIncomingTransaction(
    int Kind,
    string Originator,
    string? Text,
    bool AutoAnswer);
