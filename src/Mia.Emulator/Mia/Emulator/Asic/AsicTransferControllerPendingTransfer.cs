// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicTransferControllerPendingTransfer(
    AsicTransferControllerTransferOperation Operation,
    AsicTransferDescriptor Source,
    AsicTransferDescriptor Destination,
    byte Selector,
    byte SourceLane,
    ushort DestinationStart);
