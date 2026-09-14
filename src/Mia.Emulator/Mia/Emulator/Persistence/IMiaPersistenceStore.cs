// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

internal interface IMiaPersistenceStore
{
    ValueTask<MiaPersistenceSnapshot?> LoadAsync(
        string key,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(
        string key,
        MiaPersistenceSnapshot snapshot,
        CancellationToken cancellationToken);
}
