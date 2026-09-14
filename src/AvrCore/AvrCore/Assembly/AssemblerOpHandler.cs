// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors; Michael Conrad Tadpol Tilstra (ported from avr8js assembler.ts / Avrian-Jump)

namespace AvrCore.Assembly;

internal delegate OpResult AssemblerOpHandler(string? a, string? b, int byteOffset, Dictionary<string, int> labels);
