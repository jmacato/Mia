// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gdfs;

internal sealed record GdfsBlockCompaction(
    int PhysicalBlock,
    byte LogicalUnit,
    int OriginalEntryCount,
    int CurrentEntryCount,
    int RemovedProgrammedBytes)
{
    public int RemovedEntryCount => OriginalEntryCount - CurrentEntryCount;
}
