// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceClockConversion
{
    public const int AvrToAsicNumerator = 13;
    public const int AvrToAsicDenominator = 12;

    public static long AvrToAsic(long avrCycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(avrCycles);
        long groups = avrCycles / AvrToAsicDenominator;
        long remainder = avrCycles % AvrToAsicDenominator;
        return checked(
            groups * AvrToAsicNumerator +
            remainder * AvrToAsicNumerator / AvrToAsicDenominator);
    }
}
