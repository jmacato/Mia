// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicRomStringWalkState(byte[] output, int callback, int callbackContext)
{
    public byte[] Output { get; } = output;
    public int OutputIndex { get; set; }
    public int Callback { get; } = callback;
    public int CallbackContext { get; } = callbackContext;
}
