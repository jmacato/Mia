// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gdfs;

internal readonly record struct GdfsCompactorEntry(
    byte State,
    ushort Key,
    ushort DataOffset,
    ushort Length,
    ushort Checksum);
