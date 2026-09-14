// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal readonly record struct GdfsEntry(
    int HeaderOffset,
    byte State,
    ushort Key,
    ushort DataOffset,
    ushort Length);
