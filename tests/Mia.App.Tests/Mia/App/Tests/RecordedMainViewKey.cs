// SPDX-License-Identifier: MIT

namespace Mia.App.Tests;

internal readonly record struct RecordedMainViewKey(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask,
    bool Pressed);
