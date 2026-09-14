// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gdfs;

internal sealed record GdfsOverlayFile(byte[] Image, int ChangedBlockCount);
