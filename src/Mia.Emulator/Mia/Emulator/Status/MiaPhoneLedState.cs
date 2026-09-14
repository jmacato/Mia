// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Status;

/// <summary>
/// Electrical state of the handset lights recovered from native firmware.
/// The phone-left package contains red and green dies. The phone-right
/// package contains the blue die. The two illumination bits are kept neutral
/// because firmware and service traces switch them as a pair and do not prove
/// which physical rail feeds the LCD or keypad.
/// </summary>
internal readonly record struct MiaPhoneLedState(
    bool LeftRed,
    bool LeftGreen,
    bool RightBlue,
    bool IlluminationBit4 = false,
    bool IlluminationBit1 = false)
{
    /// <summary>
    /// Native firmware enables both recovered illumination rails together.
    /// The UI therefore lights the LCD and keypad only while either rail is
    /// asserted, without inventing an unsupported per-rail identity.
    /// </summary>
    public bool BacklightsOn => IlluminationBit4 || IlluminationBit1;
}
