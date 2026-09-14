// SPDX-License-Identifier: MIT

namespace Mia.App.Controls;

internal readonly record struct MiaPhoneInputReleaseChange(
    bool Found,
    MiaPhoneKey Key,
    bool KeyBecameInactive);
