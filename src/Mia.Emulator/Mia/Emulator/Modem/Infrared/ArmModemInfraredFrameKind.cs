// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Infrared;

internal enum ArmModemInfraredFrameKind
{
    Unknown,
    Discovery,
    SetNormalResponseMode,
    UnnumberedAcknowledgement,
    Disconnect,
    Information,
    ReceiveReady,
}
