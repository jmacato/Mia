// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewAsyncGate
{
    TaskCompletionSource? _active;
    int _accepting = 1;

    public event EventHandler? AvailabilityChanged;

    public bool IsAvailable =>
        Volatile.Read(ref _accepting) != 0 && Volatile.Read(ref _active) is null;

    public async Task<bool> TryRunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Volatile.Read(ref _accepting) == 0)
        {
            return false;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _active, completion, null) is not null)
        {
            return false;
        }

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (Volatile.Read(ref _accepting) != 0)
            {
                await operation().ConfigureAwait(true);
            }
            return true;
        }
        finally
        {
            Interlocked.CompareExchange(ref _active, null, completion);
            completion.TrySetResult();
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Close()
    {
        Interlocked.Exchange(ref _accepting, 0);
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CloseAsync()
    {
        Close();
        TaskCompletionSource? active = Volatile.Read(ref _active);
        if (active is not null)
        {
            await active.Task.ConfigureAwait(false);
        }
    }
}
