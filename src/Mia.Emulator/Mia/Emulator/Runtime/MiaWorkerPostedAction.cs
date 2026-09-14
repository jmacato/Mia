// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

#if !BROWSER || BROWSER_THREADS
internal sealed class MiaWorkerPostedAction : IMiaWorkerWorkItem
{
    readonly MiaWorker _owner;
    readonly Action _action;

    public MiaWorkerPostedAction(MiaWorker owner, Action action)
    {
        _owner = owner;
        _action = action;
    }

    public void Execute()
    {
        try
        {
            _action();
        }
        catch (Exception error) when (ReportFault(error))
        {
        }
        finally
        {
            _owner.RecordCompletedWork();
        }
    }

    bool ReportFault(Exception error)
    {
        _owner.OnFaulted(error);
        return true;
    }
}
#endif
