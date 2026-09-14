// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Infrared;

internal readonly record struct MiaInfraredTransferStatus(
    bool PortEnabled,
    MiaInfraredTransferPhase Phase,
    string Message,
    MiaTransferObject? StagedObject,
    MiaTransferObject? ReceivedObject,
    int ObjectBytesTransferred,
    long ObjectsSentToHandset,
    long ObjectsReceivedFromHandset);
