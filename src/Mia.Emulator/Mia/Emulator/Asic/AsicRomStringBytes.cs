// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRomStringBytes(byte[] Bytes, bool Terminated);
