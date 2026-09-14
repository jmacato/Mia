// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gdfs;

internal sealed record GdfsOverlayState(byte[] RawImage, int ChangedBlockCount);
