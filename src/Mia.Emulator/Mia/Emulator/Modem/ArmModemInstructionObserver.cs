// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal sealed class ArmModemInstructionObserver(
    uint address,
    Action<ArmModem> callback,
    Action<ArmModemInstructionObserver> remove) : IDisposable
{
    public uint Address { get; } = address;

    public Action<ArmModem> Callback { get; } = callback;

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        IsDisposed = true;
        remove(this);
    }
}
