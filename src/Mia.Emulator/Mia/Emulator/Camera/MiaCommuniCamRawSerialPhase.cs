// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal enum MiaCommuniCamRawSerialPhase
{
    NotStarted,
    ResetSent,
    BaudRateQuerySent,
    BaudRateCommandSent,
    CmuxQuerySent,
    CmuxCommandSent,
    Multiplexed,
}
