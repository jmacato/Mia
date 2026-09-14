// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceLimits
{
    public const int ByteValueCells = 256;
    public const int WideValueCells = 1_024;
    public const int ByteTransitionCells = 65_536;
    public const int WideTransitionCells = 4_096;
    public const int AvrProgramCounterCells = 1 << 22;
    public const int ProgramCounterCells = 4_096;
    public const int MaskCells = 256;
    public const int WideBinValueCells = 32;
}
