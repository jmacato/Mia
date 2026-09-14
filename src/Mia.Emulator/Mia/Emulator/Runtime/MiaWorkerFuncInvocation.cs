// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerFuncInvocation<TResult> : IMiaWorkerWorkItem
{
    readonly MiaWorker _owner;
    readonly Func<TResult> _function;
    readonly MiaWorkerSynchronousWaiter _waiter;
    TResult? _result;
    ExceptionDispatchInfo? _failure;

    public MiaWorkerFuncInvocation(
        MiaWorker owner,
        Func<TResult> function,
        MiaWorkerSynchronousWaiter waiter)
    {
        _owner = owner;
        _function = function;
        _waiter = waiter;
    }

    public void Execute()
    {
        try
        {
            _result = _function();
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

    public TResult Wait()
    {
        _waiter.Completed.WaitOne();
        _failure?.Throw();
        return _result!;
    }
}
#endif
