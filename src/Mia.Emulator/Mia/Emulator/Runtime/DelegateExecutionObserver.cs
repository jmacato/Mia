// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

sealed class DelegateExecutionObserver : IMiaExecutionObserver
{
    readonly Func<MiaMachine, string?> _beforeWorkItem;
    readonly Action<MiaMachine, int, AsicRomDispatchResult> _afterRomDispatch;

    public DelegateExecutionObserver(
        Func<MiaMachine, string?> beforeWorkItem,
        Action<MiaMachine, int, AsicRomDispatchResult> afterRomDispatch)
    {
        _beforeWorkItem = beforeWorkItem;
        _afterRomDispatch = afterRomDispatch;
    }

    public string? BeforeWorkItem(MiaMachine machine) =>
        _beforeWorkItem(machine);

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result) =>
        _afterRomDispatch(machine, entry, result);
}
