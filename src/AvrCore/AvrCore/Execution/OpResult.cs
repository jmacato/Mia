// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors; Michael Conrad Tadpol Tilstra (ported from avr8js assembler.ts / Avrian-Jump)

namespace AvrCore.Execution;

sealed class OpResult
{
    internal readonly List<ushort>? Words;
    internal readonly Func<Dictionary<string, int>, OpResult>? Deferred;
    internal readonly int SizeWords;

    OpResult(List<ushort>? words, Func<Dictionary<string, int>, OpResult>? deferred, int sizeWords)
    {
        Words = words;
        Deferred = deferred;
        SizeWords = sizeWords;
    }

    internal static OpResult FromWords(params ushort[] words) => new(words.ToList(), null, words.Length);

    internal static OpResult Defer(int sizeWords, Func<Dictionary<string, int>, OpResult> resolve) =>
        new(null, resolve, sizeWords);

    public static implicit operator OpResult(int opcode) => FromWords((ushort)opcode);
}
