// SPDX-License-Identifier: MIT

namespace Mia.App.Controls;

internal readonly record struct MiaPhoneKeypadContact(
    byte ScanMask,
    byte RowMask,
    byte? SecondaryScanMask = null,
    bool Power = false);
