// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class MiaPersistenceTestsGatedPersistenceStore : IMiaPersistenceStore
{
    readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    int _inFlight;

    public TaskCompletionSource FirstSaveEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public int EnteredCount;

    public bool ObservedOverlap { get; private set; }

    public ValueTask<MiaPersistenceSnapshot?> LoadAsync(
        string key,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<MiaPersistenceSnapshot?>(null);

    public async ValueTask SaveAsync(
        string key,
        MiaPersistenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _inFlight) > 1)
        {
            ObservedOverlap = true;
        }
        Interlocked.Increment(ref EnteredCount);
        FirstSaveEntered.TrySetResult();
        await _release.Task.ConfigureAwait(false);
        Interlocked.Decrement(ref _inFlight);
    }

    public void Release() => _release.TrySetResult();
}
