// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

/// <summary>
/// Latest controller-bound Bluetooth lifecycle states recovered from native
/// DSP commands. Link states are packed one byte per slot, least-significant
/// byte first; <see cref="TryGetLinkState"/> avoids exposing that encoding to
/// callers.
/// </summary>
internal readonly record struct MiaBluetoothControllerStates(
    bool HasGlobalState,
    byte GlobalState,
    byte ObservedLinkSlotMask,
    ulong PackedLinkStates)
{
    public bool TryGetLinkState(int slot, out byte state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            slot,
            MiaStatusIndicatorController.BluetoothLinkSlotCount);

        if ((ObservedLinkSlotMask & (1 << slot)) == 0)
        {
            state = 0;
            return false;
        }

        state = (byte)(PackedLinkStates >> (slot * 8));
        return true;
    }
}
