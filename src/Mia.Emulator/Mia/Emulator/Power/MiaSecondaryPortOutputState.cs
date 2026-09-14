// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Power;

/// <summary>
/// Raw firmware-visible output latches retained by the secondary primary-bus
/// device. Meanings are deliberately not assigned until a native producer and
/// a physical function have both been recovered.
/// </summary>
internal readonly record struct MiaSecondaryPortOutputState(
    byte Register40,
    byte Register48,
    byte Register80);
