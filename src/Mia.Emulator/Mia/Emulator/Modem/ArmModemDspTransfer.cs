// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemDspTransfer(
    byte Control,
    byte? Kind,
    byte Trailer,
    byte[] Payload);
