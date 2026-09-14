// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem;

internal sealed class ArmModemBusExternalRamWriteObserver(
    uint address,
    Action<ArmModemRamWrite> callback,
    Action<ArmModemBusExternalRamWriteObserver> remove) : IDisposable
{
    public uint Address { get; } = address;

    public Action<ArmModemRamWrite> Callback { get; } = callback;

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
