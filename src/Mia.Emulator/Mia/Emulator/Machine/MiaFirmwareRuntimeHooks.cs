// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

/// <summary>
/// Resolves firmware-version-specific execution hooks from verified opcode
/// signatures. A hook is usable only after the execution path separately
/// establishes its runtime fixed point and event-safety conditions.
/// </summary>
internal static class MiaFirmwareRuntimeHooks
{
    internal static ReadOnlySpan<ushort> IdleLoopWords =>
    [
        0x94f8, 0xe000, 0xbf0b, 0x9100, 0xf603,
        0x3001, 0xf069, 0xe0e2, 0xbfeb, 0xedff,
        0xe0ec, 0x8100, 0x6200, 0xe0e0, 0xbfeb,
        0xe0fa, 0xece0, 0x8300, 0x9478, 0xcfec,
    ];

    static ReadOnlySpan<ushort> GdfsSectorHeaderLoopWords =>
    [
        0x2044, 0xf009, 0xc044, 0x3040, 0xe000, 0x0750,
        0xe001, 0x0760, 0xe000, 0x0770, 0xf5e0, 0x2f28,
        0x2711, 0x0f04, 0x1f15, 0x1f26, 0x5000, 0x4010,
        0x4228, 0x2fe0, 0x2ff1, 0xbf2b, 0x8001, 0x8012,
        0x1406, 0x0417, 0xf539,
    ];

    static ReadOnlySpan<ushort> GdfsSectorHeaderLoopTailWords =>
        [0x5f47, 0x4f5f, 0x4f6f, 0x4f7f, 0xcfb9];

    internal static ReadOnlySpan<ushort> GdfsUnitStateLoopWords =>
        [0xbf3b, 0x8110, 0x2311, 0xf411, 0x9731, 0xcffa];

    internal static bool HasKnownGdfsUnitStateLoop(ReadOnlySpan<byte> firmware) =>
        MatchesWordsAt(firmware,
            AsicFirmwareFastPaths.GdfsUnitStateScanWord * sizeof(ushort),
            GdfsUnitStateLoopWords);

    /// <summary>
    /// Finds the firmware scheduler's proven idle-loop sequence. Requiring a
    /// unique, word-aligned match lets compatible firmware revisions move the
    /// loop without turning an arbitrary byte sequence into an execution hook.
    /// </summary>
    internal static int ResolveIdleLoopStartWord(ReadOnlySpan<byte> firmware)
    {
        var words = IdleLoopWords;
        int byteLength = checked(words.Length * sizeof(ushort));
        if (firmware.Length < byteLength)
        {
            return -1;
        }

        int match = -1;
        for (int offset = 0; offset <= firmware.Length - byteLength; offset += 2)
        {
            if (!MatchesWordsAt(firmware, offset, words))
            {
                continue;
            }

            if (match >= 0)
            {
                return -1;
            }

            match = offset / 2;
        }

        return match;
    }

    /// <summary>
    /// Confirms the one GDFS scan fast path against every instruction it can
    /// bypass. This is intentionally version-specific: unlike the idle hook,
    /// the replacement has an exact CPU-state proof for this firmware body.
    /// </summary>
    internal static bool HasKnownGdfsSectorHeaderLoop(
        ReadOnlySpan<byte> firmware)
    {
        const int LoopStartWord = AsicFirmwareFastPaths.GdfsSectorHeaderScanWord;
        const int LoopTailStartWord = 0x0cb4e6;
        return MatchesWordsAt(
                   firmware,
                   LoopStartWord * sizeof(ushort),
                   GdfsSectorHeaderLoopWords) &&
               MatchesWordsAt(
                   firmware,
                   LoopTailStartWord * sizeof(ushort),
                   GdfsSectorHeaderLoopTailWords);
    }

    static bool MatchesWordsAt(
        ReadOnlySpan<byte> firmware,
        int byteOffset,
        ReadOnlySpan<ushort> words)
    {
        int byteLength = checked(words.Length * sizeof(ushort));
        if (byteOffset < 0 || byteOffset > firmware.Length - byteLength)
        {
            return false;
        }
        for (int index = 0; index < words.Length; index++)
        {
            ushort word = words[index];
            int offset = byteOffset + index * sizeof(ushort);
            if (firmware[offset] != (byte)word ||
                firmware[offset + 1] != (byte)(word >> 8))
            {
                return false;
            }
        }

        return true;
    }
}
