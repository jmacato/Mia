// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Firmware-visible software request latches for the time-generator
/// high-priority processes whose set/clear pairing is proven. Firmware sets a
/// bit in 0x08c8 to request a process, and that process clears the same bit
/// before returning.
/// </summary>
internal sealed class AsicHighPriorityRequests
{
    public const int RequestAddress = 0x08c8;
    public const byte Sc0Request = 0x08;
    public const byte Sc1Request = 0x10;
    public const byte AdcRequest = 0x40;
    public const byte ProvenRequestMask =
        Sc0Request | Sc1Request | AdcRequest;

    readonly AsicInterruptController _interruptController;

    public AsicHighPriorityRequests(
        Cpu cpu,
        AsicInterruptController interruptController)
    {
        _interruptController = interruptController;

        var previousHook = cpu.WriteHooks[RequestAddress];
        cpu.WriteHooks[RequestAddress] = (value, oldValue, address, mask) =>
        {
            var newValue = (byte)((oldValue & ~mask) | (value & mask));
            RaiseNewRequests((byte)(newValue & ~oldValue & ProvenRequestMask));
            return previousHook?.Invoke(value, oldValue, address, mask) ?? false;
        };
    }

    public long RequestCount { get; private set; }

    void RaiseNewRequests(byte requests)
    {
        RaiseIfSet(requests, Sc0Request, AsicInterruptController.TimeGeneratorSc0Source);
        RaiseIfSet(requests, Sc1Request, AsicInterruptController.TimeGeneratorSc1Source);
        RaiseIfSet(requests, AdcRequest, AsicInterruptController.TimeGeneratorAdcSource);
    }

    void RaiseIfSet(byte requests, byte request, byte source)
    {
        if ((requests & request) == 0)
        {
            return;
        }

        RequestCount++;
        _interruptController.RaiseHighPriority(source);
    }
}
