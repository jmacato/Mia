// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerConcurrentInvocation : IMiaWorkerWorkItem
{
    readonly MiaWorker _owner;
    readonly Action _action;
    readonly MiaWorkerConcurrentWaiter _waiter;
    ExceptionDispatchInfo? _failure;
    int _completed;

    internal MiaWorkerConcurrentInvocation(
        MiaWorker owner,
        Action action,
        MiaWorkerConcurrentWaiter waiter)
    {
        _owner = owner;
        _action = action;
        _waiter = waiter;
    }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public void Execute()
    {
        try
        {
            _action();
        }
        catch (Exception error) when (CaptureFailure(error))
        {
        }
        finally
        {
            _owner.RecordCompletedWork();
            Volatile.Write(ref _completed, 1);
            _waiter.Completed.Set();
        }
    }

    bool CaptureFailure(Exception error)
    {
        _failure = ExceptionDispatchInfo.Capture(error);
        return true;
    }

    public void Wait()
    {
        // Always consume the auto-reset signal, including when completion
        // won the race with this call, so the caller-local waiter is clean
        // before its next grant.
        _waiter.Completed.WaitOne();
        _waiter.InUse = false;
        _failure?.Throw();
    }
}
#endif
