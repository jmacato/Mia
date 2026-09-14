// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemBusScheduledUartTransmission(
    byte Value,
    long CompletionCycle);
