// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal readonly record struct AccessTraceAddressKey(
    AccessTraceBus Bus,
    ulong Address,
    int Size,
    byte? Device = null,
    byte? Register = null);
