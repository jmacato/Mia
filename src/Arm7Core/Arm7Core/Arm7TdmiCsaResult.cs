// SPDX-License-Identifier: Zlib
//
// C# port of the ARM7TDMI Booth multiplier reconstruction from
// https://github.com/zaydlang/multiplication-algorithm.
// Copyright (c) 2024 zaydlang. Contributions by calc84maniac.
// This is an altered source version; see LICENSE.Multiplier.txt.

namespace Arm7Core;

internal readonly record struct Arm7TdmiCsaResult(ulong Output, ulong Carry);
