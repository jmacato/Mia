// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Runtime;

internal readonly record struct EmulatorSessionInputCommand(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask,
    bool Power,
    bool Pressed);
