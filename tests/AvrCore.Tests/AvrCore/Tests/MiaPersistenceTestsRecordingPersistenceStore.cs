// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class MiaPersistenceTestsRecordingPersistenceStore : IMiaPersistenceStore
{
    public string? SavedKey { get; private set; }

    public MiaPersistenceSnapshot? SavedSnapshot { get; private set; }

    public ValueTask<MiaPersistenceSnapshot?> LoadAsync(
        string key,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<MiaPersistenceSnapshot?>(null);

    public ValueTask SaveAsync(
        string key,
        MiaPersistenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        SavedKey = key;
        SavedSnapshot = snapshot;
        return ValueTask.CompletedTask;
    }
}
