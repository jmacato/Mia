// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal readonly record struct AdcProgram(
    byte ActionId,
    ushort Operand,
    int Port);
