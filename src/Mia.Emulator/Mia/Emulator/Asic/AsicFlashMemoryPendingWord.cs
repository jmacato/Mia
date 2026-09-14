// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicFlashMemoryPendingWord(
    int Address,
    ushort Word,
    long ProgramVersion,
    bool CompletesErase);
