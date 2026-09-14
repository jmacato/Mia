// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Persistence;

internal sealed record GdfsOverlayLoad(
    ReadOnlyMemory<byte> RawImage,
    int ChangedBlockCount,
    bool FromOverlay,
    string? RecoveryMessage = null);
