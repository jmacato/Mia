// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Gsm;

/// <summary>
/// Compatibility adapter for the dynamically verified R8A015 pending CHANNEL
/// REQUEST table. This is not an ASIC hardware claim: it lets the source-level
/// cell recover the request reference while the transmit-side private channel
/// encoder contract remains unknown. The response still enters through the
/// normal channel-decoder MMIO completion and interrupt path.
/// </summary>
internal static class MiaGsmRandomAccessAdapter
{
    public const int PendingCountAddress = 0x03391e;
    public const int RequestReferenceAddress = 0x033922;
    public const int T2Address = 0x033928;
    public const int T1PrimeAddress = 0x03392b;
    public const int T3Address = 0x03392e;
    public const int MaximumPendingCount = 3;

    public static GsmRandomAccessReference? ReadPendingReference(Cpu cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (cpu.Data.Length <= T3Address + MaximumPendingCount - 1)
        {
            return null;
        }

        var count = cpu.ReadData(PendingCountAddress);
        if (count is 0 or > MaximumPendingCount)
        {
            return null;
        }

        for (var index = 0; index < count; index++)
        {
            var requestWord = cpu.ReadData(RequestReferenceAddress + index * 2) |
                cpu.ReadData(RequestReferenceAddress + index * 2 + 1) << 8;
            var t2 = cpu.ReadData(T2Address + index);
            var t1Prime = cpu.ReadData(T1PrimeAddress + index);
            var t3 = cpu.ReadData(T3Address + index);
            if (requestWord <= byte.MaxValue &&
                t1Prime <= 31 &&
                t3 <= 50 &&
                t2 <= 25)
            {
                return new(
                    (byte)requestWord,
                    t1Prime,
                    t3,
                    t2);
            }
        }

        return null;
    }
}
