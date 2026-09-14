// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

internal readonly record struct MiaCommuniCamSerialTransmission(
    byte[] Bytes,
    long FirstByteDelayCycles,
    long CharacterCycles);
