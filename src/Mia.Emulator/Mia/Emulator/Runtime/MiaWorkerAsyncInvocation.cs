// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerAsyncInvocation : IMiaWorkerWorkItem
{
    readonly MiaWorker _owner;
    readonly Action _action;
    readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public MiaWorkerAsyncInvocation(MiaWorker owner, Action action)
    {
        _owner = owner;
        _action = action;
    }

    public Task Completion => _completion.Task;

    public void Execute()
    {
        Exception? failure = null;
        try
        {
            _action();
        }
        catch (Exception error) when (Capture(error, out failure))
        {
        }
        _owner.RecordCompletedWork();
        if (failure is null)
        {
            _completion.SetResult();
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
