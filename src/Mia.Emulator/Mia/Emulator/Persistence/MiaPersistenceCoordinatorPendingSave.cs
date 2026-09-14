// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

internal readonly record struct MiaPersistenceCoordinatorPendingSave(
    long Version,
    MiaPersistenceSnapshot Snapshot);
