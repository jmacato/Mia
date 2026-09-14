// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerActionInvocation : IMiaWorkerWorkItem
{
    readonly MiaWorker _owner;
    readonly Action _action;
    readonly MiaWorkerSynchronousWaiter _waiter;
    ExceptionDispatchInfo? _failure;

    public MiaWorkerActionInvocation(
        MiaWorker owner,
        Action action,
        MiaWorkerSynchronousWaiter waiter)
    {
        _owner = owner;
        _action = action;
        _waiter = waiter;
    }

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
        _waiter.Completed.WaitOne();
        _failure?.Throw();
    }
}
#endif
