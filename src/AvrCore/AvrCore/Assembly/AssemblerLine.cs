// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors; Michael Conrad Tadpol Tilstra (ported from avr8js assembler.ts / Avrian-Jump)

namespace AvrCore.Assembly;

internal sealed class AssemblerLine
{
    public int Line { get; internal set; }
    public string Text { get; internal set; } = "";
    public int ByteOffset { get; internal set; }
    public IReadOnlyList<ushort>? Words { get; internal set; }

    internal Func<Dictionary<string, int>, OpResult>? Deferred;
    internal int SizeWords;
}
