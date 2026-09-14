// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicFchRequest(
    long StartCycle,
    IReadOnlyList<AsicRfTransaction> RfTransactions);
