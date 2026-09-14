// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicRomDecimalParseState(int address)
{
    public int Address { get; } = address;

    public int Offset { get; set; }

    public bool Negative { get; set; }

    public uint Value { get; set; }
}
