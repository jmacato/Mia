// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gdfs;

internal sealed record GdfsCompactionResult(
    byte[] Image,
    IReadOnlyList<GdfsBlockCompaction> Blocks)
{
    public int RemovedEntryCount => Blocks.Sum(block => block.RemovedEntryCount);
    public int RemovedProgrammedBytes => Blocks.Sum(block => block.RemovedProgrammedBytes);
}
