// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

internal sealed record MiaPersistenceCoordinatorState(
    MiaPersistenceCoordinatorPendingSave? PendingSave,
    TaskCompletionSource? IdleCompletion,
    long LastCapturedVersion,
    long LastPersistedVersion,
    bool SaveInFlight,
    Exception? LastSaveError);
