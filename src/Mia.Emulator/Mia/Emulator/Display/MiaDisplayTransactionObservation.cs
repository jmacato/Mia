// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Display;

internal readonly record struct MiaDisplayTransactionObservation(
    byte Address,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> Frame,
    long Cycle,
    int Pc,
    int SchedulerRecord,
    long DmaCycle,
    int DmaPc,
    int DmaSource,
    int DmaLength);
