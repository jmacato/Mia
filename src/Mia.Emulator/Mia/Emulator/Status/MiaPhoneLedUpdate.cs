// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Status;

internal readonly record struct MiaPhoneLedUpdate(
    bool? LeftRed = null,
    bool? LeftGreen = null,
    bool? RightBlue = null,
    bool? IlluminationBit4 = null,
    bool? IlluminationBit1 = null);
