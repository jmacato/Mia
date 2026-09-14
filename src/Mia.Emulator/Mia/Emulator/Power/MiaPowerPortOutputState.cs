// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Power;

internal readonly record struct MiaPowerPortOutputState(
    byte PortA3,
    byte PortA4,
    byte PortA5,
    byte PortA6,
    byte PortA7,
    byte PortA9,
    byte PowerControl,
    byte PortB0);
