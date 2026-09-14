// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Input;

internal readonly record struct InteractiveKeypadContact(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask = null,
    bool Power = false);
