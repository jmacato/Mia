// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemDspPacket(byte Type, byte[] Payload);
