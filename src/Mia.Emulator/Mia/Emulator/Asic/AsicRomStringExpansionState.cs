// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicRomStringExpansionState(
    int source,
    int argumentCursorAddress,
    int argumentCursor)
{
    public int Source { get; set; } = source;

    public int ArgumentCursorAddress { get; } = argumentCursorAddress;

    public int ArgumentCursor { get; set; } = argumentCursor;

    public List<byte> Bytes { get; } = [];
}
