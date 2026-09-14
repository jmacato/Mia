// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal readonly record struct ArmModemBusScheduledUartReceive(byte Value, long ArrivalCycle);
