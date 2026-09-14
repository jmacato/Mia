// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

/// <summary>
/// Exposes the firmware's proven instruction-fetch alias of its flash-driver
/// RAM buffer without changing the NOR flash at the overlapping byte address.
/// </summary>
internal static class AsicExecutableRam
{
    public const int DataStart = 0x036aa4;
    public const int ProgramWordStart = 0x3dad52;
    public const int Length = 0x180;

    public static void Attach(Cpu cpu)
    {
        var previousHook = cpu.ProgramWordReadHook;
        var previousStart = cpu.ProgramWordReadHookStart;
        var previousEnd = cpu.ProgramWordReadHookEnd;
        cpu.ProgramWordReadHook = wordIndex =>
        {
            var offset = (wordIndex - ProgramWordStart) * 2;
            if (offset < 0 || offset + 1 >= Length)
            {
                return previousHook?.Invoke(wordIndex);
            }

            var dataAddress = DataStart + offset;
            return cpu.ReadData(dataAddress) | cpu.ReadData(dataAddress + 1) << 8;
        };
        if (previousHook is null)
        {
            cpu.ProgramWordReadHookStart = ProgramWordStart;
            cpu.ProgramWordReadHookEnd = ProgramWordStart + Length / 2;
        }
        else
        {
            cpu.ProgramWordReadHookStart = Math.Min(previousStart, ProgramWordStart);
            cpu.ProgramWordReadHookEnd = Math.Max(
                previousEnd,
                ProgramWordStart + Length / 2);
        }
    }
}
