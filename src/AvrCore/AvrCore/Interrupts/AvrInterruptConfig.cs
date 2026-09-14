// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js cpu.ts)

namespace AvrCore.Interrupts;

public class AvrInterruptConfig
{
    public int Address { get; init; }
    public int EnableRegister { get; init; }
    public byte EnableMask { get; init; }
    public int FlagRegister { get; init; }
    public byte FlagMask { get; init; }
    public bool Constant { get; init; }
    public bool InverseFlag { get; init; }
}
