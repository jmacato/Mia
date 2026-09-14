// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

/// <summary>
/// Raw values returned through equalizer result registers 0x0923..0x0929.
/// Their original private units are not recovered, so compatibility sources
/// keep the register-shaped contract explicit.
/// </summary>
internal readonly record struct AsicEqualizerResult(
    byte Result23,
    byte Result24,
    byte Result25,
    byte Result26,
    byte Result27,
    byte Result28,
    byte Result29)
{
    public static AsicEqualizerResult Invalid { get; } =
        new(0, AsicEqualizer.InvalidResultMinimum, 0, 0, 0, 0, 0);
}
