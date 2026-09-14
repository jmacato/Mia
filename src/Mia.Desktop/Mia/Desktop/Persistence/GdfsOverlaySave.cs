// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Persistence;

internal sealed record GdfsOverlaySave(
    int ChangedBlockCount,
    long FileLength);
