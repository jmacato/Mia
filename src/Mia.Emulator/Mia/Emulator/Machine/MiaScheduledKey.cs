// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

internal readonly record struct MiaScheduledKey(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask,
    bool Pressed,
    bool RaiseInterrupt);
