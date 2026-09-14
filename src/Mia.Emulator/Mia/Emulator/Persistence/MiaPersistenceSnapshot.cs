// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

internal sealed record MiaPersistenceSnapshot(
    int Version,
    SimFileOverlay[] SimFiles)
{
    public const int CurrentVersion = 1;

    public static MiaPersistenceSnapshot Empty { get; } = new(
        CurrentVersion,
        []);
}
