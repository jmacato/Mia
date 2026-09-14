// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal enum ArmModemStopKind
{
    None,
    UnknownRomCall,
    UnmappedBusAccess,
    UndefinedInstruction,
}
