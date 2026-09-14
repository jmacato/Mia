// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

/// <summary>
/// Coalesces firmware-produced persistence changes and writes them while the
/// emulator is running, so browser shutdown is not the only save boundary.
/// </summary>
internal sealed class MiaPersistenceCoordinator
{
    readonly MiaPersistenceSession _session;
    MiaPersistenceCoordinatorState _state = new(
        PendingSave: null,
        IdleCompletion: null,
        LastCapturedVersion: -1,
        LastPersistedVersion: -1,
        SaveInFlight: false,
        LastSaveError: null);

    public MiaPersistenceCoordinator(MiaPersistenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public event Action<MiaPersistenceSnapshot>? SnapshotSaved;

    public event Action<Exception>? SaveFailed;

    public void MarkLoaded(MiaMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        long version = machine.PersistenceVersion;
        UpdateState(current => current with
        {
            LastCapturedVersion = version,
            LastPersistedVersion = version,
            LastSaveError = null,
        });
    }

    public void ScheduleSave(MiaMachine machine, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(machine);
        long currentVersion = machine.PersistenceVersion;
        if (currentVersion == Volatile.Read(ref _state).LastCapturedVersion)
        {
            return;
        }

        MiaPersistenceCapture capture = machine.CapturePersistenceSnapshot();
        (MiaPersistenceCoordinatorState previous, MiaPersistenceCoordinatorState next) = UpdateState(current =>
            current.LastCapturedVersion == capture.Version
                ? current
                : current with
                {
                    LastCapturedVersion = capture.Version,
                    LastSaveError = null,
                    PendingSave = new MiaPersistenceCoordinatorPendingSave(capture.Version, capture.Snapshot),
                    SaveInFlight = true,
                });

        bool startSave = !ReferenceEquals(previous, next) && !previous.SaveInFlight;
        if (startSave)
        {
            DrainSaves();
        }
    }

    public async Task FlushAsync(MiaMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ScheduleSave(machine, force: true);
        while (true)
        {
            (_, MiaPersistenceCoordinatorState next) = UpdateState(current =>
                current.SaveInFlight || current.PendingSave is not null
                    ? current with
                    {
                        IdleCompletion = current.IdleCompletion ?? new(
                            TaskCreationOptions.RunContinuationsAsynchronously),
                    }
                    : current);

            if (next.SaveInFlight || next.PendingSave is not null)
            {
                await next.IdleCompletion!.Task.ConfigureAwait(false);
                continue;
            }

            long currentVersion = machine.PersistenceVersion;
            if (next.LastSaveError is not null &&
                currentVersion != next.LastPersistedVersion)
            {
                throw new IOException(
                    "Could not save the Mia persistence snapshot.",
                    next.LastSaveError);
            }
            if (currentVersion == next.LastPersistedVersion)
            {
                return;
            }
            ScheduleSave(machine, force: true);
        }
    }

    void DrainSaves()
    {
        while (true)
        {
            (MiaPersistenceCoordinatorState previous, _) = UpdateState(current =>
                current.PendingSave is null
                    ? current with { SaveInFlight = false, IdleCompletion = null }
                    : current with { PendingSave = null });

            if (previous.PendingSave is null)
            {
                previous.IdleCompletion?.SetResult();
                return;
            }

            MiaPersistenceCoordinatorPendingSave save = previous.PendingSave.Value;
            try
            {
                ValueTask saveTask = _session.Store.SaveAsync(
                    _session.Key,
                    save.Snapshot,
                    CancellationToken.None);
                if (!saveTask.IsCompleted)
                {
                    _ = AwaitSaveAsync(saveTask, save);
                    return;
                }
                saveTask.GetAwaiter().GetResult();
                CompleteSave(save);
            }
            catch (Exception error) when (FailSaveAndContinue(save, error))
            {
            }
        }
    }

    async Task AwaitSaveAsync(ValueTask saveTask, MiaPersistenceCoordinatorPendingSave save)
    {
        try
        {
            await saveTask.ConfigureAwait(false);
            CompleteSave(save);
        }
        catch (Exception error) when (FailSaveAndContinue(save, error))
        {
        }
        DrainSaves();
    }

    bool FailSaveAndContinue(MiaPersistenceCoordinatorPendingSave save, Exception error)
    {
        FailSave(save, error);
        return true;
    }

    void CompleteSave(MiaPersistenceCoordinatorPendingSave save)
    {
        UpdateState(current => current with
        {
            LastPersistedVersion = save.Version,
            LastSaveError = null,
        });
        NotifySnapshotSaved(save.Snapshot);
    }

    void FailSave(MiaPersistenceCoordinatorPendingSave save, Exception error)
    {
        UpdateState(current =>
            current.PendingSave is null && current.LastCapturedVersion == save.Version
                ? current with
                {
                    LastSaveError = error,
                    LastCapturedVersion = current.LastPersistedVersion,
                }
                : current with { LastSaveError = error });
        NotifySaveFailed(error);
    }

    void NotifySnapshotSaved(MiaPersistenceSnapshot snapshot)
    {
        try
        {
            SnapshotSaved?.Invoke(snapshot);
        }
        catch (Exception observerError) when (IgnoreObserverFailure(observerError))
        {
        }
    }

    void NotifySaveFailed(Exception error)
    {
        try
        {
            SaveFailed?.Invoke(error);
        }
        catch (Exception observerError) when (IgnoreObserverFailure(observerError))
        {
        }
    }

    // Observer failures must not strand the persistence drain.
    static bool IgnoreObserverFailure(Exception error) => true;

    (MiaPersistenceCoordinatorState Previous, MiaPersistenceCoordinatorState Next) UpdateState(
        Func<MiaPersistenceCoordinatorState, MiaPersistenceCoordinatorState> transform)
    {
        while (true)
        {
            MiaPersistenceCoordinatorState previous = Volatile.Read(ref _state);
            MiaPersistenceCoordinatorState next = transform(previous);
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _state, next, previous),
                previous))
            {
                return (previous, next);
            }
        }
    }

}
