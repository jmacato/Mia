// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerAsyncFuncInvocation<TResult> : IMiaWorkerWorkItem
{
    readonly MiaWorker _owner;
    readonly Func<TResult> _function;
    readonly TaskCompletionSource<TResult> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public MiaWorkerAsyncFuncInvocation(MiaWorker owner, Func<TResult> function)
    {
        _owner = owner;
        _function = function;
    }

    public Task<TResult> Completion => _completion.Task;

    public void Execute()
    {
        TResult? result = default;
        Exception? failure = null;
        try
        {
            result = _function();
        }
        catch (Exception error) when (Capture(error, out failure))
        {
        }
        _owner.RecordCompletedWork();
        if (failure is null)
        {
            _completion.SetResult(result!);
        }
        else
        {
            _completion.SetException(failure);
        }
    }

    static bool Capture(Exception error, out Exception failure)
    {
        failure = error;
        return true;
    }
}
#endif
