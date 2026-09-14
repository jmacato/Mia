// SPDX-License-Identifier: MIT

namespace Mia.App.Controls;

internal readonly record struct MiaPhoneInputPressChange(
    bool SourceChanged,
    MiaPhoneKey? PreviousKey,
    bool PreviousKeyBecameInactive,
    bool KeyBecameActive);
