// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Infrared;

internal enum MiaInfraredTransferPhase
{
    Idle,
    WaitingForPort,
    Discovering,
    ConnectingLink,
    QueryingServices,
    ConnectingObjectPush,
    SendingObject,
    ReceivingObject,
    Disconnecting,
    Completed,
    Failed,
}
