// SPDX-License-Identifier: MIT

namespace AvrCore.Assembly;

internal readonly record struct ProgramMemoryEncoding(
    int NoOperandOpcode,
    int ZCode,
    int ZPlusCode);
