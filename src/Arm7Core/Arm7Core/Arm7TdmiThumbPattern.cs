// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Arm7Core;

internal readonly record struct Arm7TdmiThumbPattern(Arm7TdmiThumbOperation Operation, string Bits);
