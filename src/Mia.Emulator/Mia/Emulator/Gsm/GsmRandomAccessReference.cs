// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly record struct GsmRandomAccessReference(
    byte RequestReference,
    byte T1Prime,
    byte T3,
    byte T2);
