// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

internal sealed class AsicFirmwareController
{
    const int CommandAddress = 0x08e0;
    const int StatusAddress = 0x08e1;
    const byte StartTransferCommand = 0x23;
    const byte CommandCodeMask = 0x7f;
    const byte TransferComplete = 0x01;

    byte status;

    public AsicFirmwareController(Cpu cpu)
    {
        var previousCommandHook = cpu.WriteHooks[CommandAddress];
        cpu.WriteHooks[CommandAddress] = (value, oldValue, address, mask) =>
        {
            if ((value & CommandCodeMask) == StartTransferCommand)
            {
                status |= TransferComplete;
            }
            else if (value == 0)
            {
                status = 0;
            }
            return previousCommandHook?.Invoke(value, oldValue, address, mask) ?? false;
        };

        var previousStatusHook = cpu.ReadHooks[StatusAddress];
        cpu.ReadHooks[StatusAddress] = address =>
            (byte)((previousStatusHook?.Invoke(address) ?? cpu.Data[address]) | status);
    }
}
