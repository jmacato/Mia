// SPDX-License-Identifier: MIT

using Avalonia.Threading;

namespace Mia.Browser.Persistence;

internal sealed class BrowserMiaPersistenceStore : IMiaPersistenceStore
{
    public ValueTask<MiaPersistenceSnapshot?> LoadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "Browser persistence must load on the UI thread.");
        }
        return ValueTask.FromResult(
            BrowserPersistenceInterop.Load(key, cancellationToken));
    }

    public ValueTask SaveAsync(
        string key,
        MiaPersistenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            BrowserPersistenceInterop.Save(key, snapshot, cancellationToken);
            return ValueTask.CompletedTask;
        }
        return new ValueTask(Dispatcher.UIThread.InvokeAsync(
            () => BrowserPersistenceInterop.Save(
                key,
                snapshot,
                cancellationToken)).GetTask());
    }
}
