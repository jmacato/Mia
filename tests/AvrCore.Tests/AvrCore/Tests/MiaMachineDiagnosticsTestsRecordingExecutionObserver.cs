// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class MiaMachineDiagnosticsTestsRecordingExecutionObserver : IMiaExecutionObserver
{
    public Func<MiaMachine, string?>? Before { get; init; }

    public int StopBeforeCall { get; init; } = int.MaxValue;

    public int BeforeCalls { get; private set; }

    public List<int> ThreadIds { get; } = [];

    public List<MiaMachineDiagnosticsTestsRomDispatchObservation> RomDispatches { get; } = [];

    public string? BeforeWorkItem(MiaMachine machine)
    {
        BeforeCalls++;
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        return BeforeCalls == StopBeforeCall
            ? Before?.Invoke(machine)
            : null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        RomDispatches.Add(new(
            entry,
            result,
            machine.IsStopped,
            Environment.CurrentManagedThreadId));
    }
}
