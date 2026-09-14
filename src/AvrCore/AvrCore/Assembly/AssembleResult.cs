// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors; Michael Conrad Tadpol Tilstra (ported from avr8js assembler.ts / Avrian-Jump)

namespace AvrCore.Assembly;

internal sealed class AssembleResult
{
    public byte[] Bytes { get; internal set; } = Array.Empty<byte>();
    public List<string> Errors { get; } = new();
    public List<AssemblerLine> Lines { get; } = new();
    public Dictionary<string, int> Labels { get; } = new();
}
