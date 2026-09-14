// SPDX-License-Identifier: MIT

using AvrCore;

namespace Mia.Emulator.Asic;

internal static class AsicFirmwareFastPaths
{
    public const int BusyWaitLoopWord = 0x018901;
    public const int BusyWaitReturnWord = 0x01890b;
    public const int GdfsUnitStateScanWord = 0x0cb0c8;
    public const int GdfsUnitStateFoundWord = 0x0cb0ce;
    public const int GdfsErasedRangeScanWord = 0x0cbe64;
    public const int GdfsErasedRangeFoundWord = 0x0cbe6d;
    public const int GdfsErasedRangeCompleteWord = 0x0cbe80;
    public const int GdfsSectorHeaderScanWord = 0x0cb4a4;
    public const int GdfsSectorHeaderMatchWord = 0x0cb4bf;
    const int GdfsSectorHeaderMismatchCycles = 36;
    const int GdfsSectorHeaderMatchCycles = 29;
    public const int GdfsEntryHeaderScanWord = 0x0cbfc1;
    public const int GdfsLinkedRecordScanWord = 0x0cd12d;
    public const int GdfsLinkedRecordMatchWord = 0x0cd142;
    public const int GdfsLinkedExtentScanWord = 0x0cd1f0;
    public const int GdfsLinkedExtentMatchWord = 0x0cd201;
    const int LogicalTableSentinel = 0xc6c581;

    /// <summary>
    /// Collapses the firmware's side-effect-free seven-NOP delay loop while
    /// preserving its register, status-flag, PC, and cycle results. This is an
    /// execution optimization only; the ordinary interpreter remains the
    /// default and all callers still execute their native code around it.
    /// </summary>
    public static bool TrySkipBusyWait(Cpu cpu)
    {
        if (cpu.PC != BusyWaitLoopWord)
        {
            return false;
        }

        var remaining = cpu.GetUint16(16);
        if (remaining == 0)
        {
            return false;
        }

        // Each non-final iteration is seven NOPs, SUBI, SBCI, and a taken
        // BRNE (11 cycles); the final branch costs one cycle instead of two.
        cpu.Cycles += 11L * remaining - 1;
        cpu.Data[16] = 0;
        cpu.Data[17] = 0;
        // The final 0x0001 -> 0x0000 SUBI/SBCI pair sets Z and clears the
        // arithmetic flags while leaving the interrupt and transfer bits.
        cpu.Data[0x5f] = (byte)((cpu.Data[0x5f] & 0xc0) | 0x02);
        cpu.PC = BusyWaitReturnWord;
        return true;
    }

    public static bool TrySkipGdfsUnitStateZeroRun(Cpu cpu) =>
        TrySkipGdfsUnitStateZeroRun(cpu, long.MaxValue, out _);

    public static bool TrySkipGdfsUnitStateZeroRun(
        Cpu cpu, long maximumCycles, out int skippedInstructions)
    {
        skippedInstructions = 0;
        if (cpu.PC != GdfsUnitStateScanWord || cpu.Data[19] != 0xc6)
        {
            return false;
        }

        var pointer = 0xc60000 | cpu.GetUint16(30);
        long cycles = 0;
        while (pointer >= LogicalTableSentinel && maximumCycles - cycles >= 9)
        {
            byte value = cpu.ReadData(pointer);
            if (value != 0)
            {
                cpu.Data[17] = value;
                cpu.Data[0x5b] = 0xc6;
                cpu.SetUint16(30, pointer);
                // OUT, LD, TST, taken BRNE. TST preserves H, C, T and I.
                int preserved = cycles == 0 ? cpu.Data[0x5f] & 0xe1 :
                    (cpu.Data[0x5f] & 0xc0) | ((pointer & 1) << 5);
                cpu.Data[0x5f] = (byte)(preserved |
                    ((value & 0x80) != 0 ? 0x14 : 0));
                cpu.Cycles += cycles + 6;
                skippedInstructions += 4;
                cpu.PC = GdfsUnitStateFoundWord;
                return true;
            }
            if (pointer == LogicalTableSentinel) break;
            pointer--;
            cycles += 9;
            skippedInstructions += 6;
        }
        if (skippedInstructions == 0)
        {
            return false;
        }

        cpu.SetUint16(30, pointer);
        cpu.Data[17] = 0;
        cpu.Data[0x5b] = 0xc6;
        // The bounded table lies in the upper half of the Z address space:
        // retain the interpreter's SBIW status at the final decrement.
        cpu.Data[0x5f] = (byte)((cpu.Data[0x5f] & 0xc0) | 0x14 |
            ((pointer & 1) << 5));
        cpu.Cycles += cycles;
        return true;
    }

    public static bool TrySkipGdfsErasedRange(Cpu cpu)
    {
        if (cpu.PC != GdfsErasedRangeScanWord)
        {
            return false;
        }

        var cursor = GetUInt32(cpu, 20);
        var end = GetUInt32(cpu, 0);
        var bank = cpu.Data[19];
        var pointer = cpu.GetUint16(30);
        while (cursor < end)
        {
            var value = cpu.ReadData(bank << 16 | pointer);
            if (value != 0xff)
            {
                cpu.Data[17] = value;
                cpu.Data[0x5b] = bank;
                SetUInt32(cpu, 20, cursor);
                cpu.SetUint16(30, pointer);
                cpu.PC = GdfsErasedRangeFoundWord;
                return true;
            }

            cursor++;
            pointer = (pointer + 1) & 0xffff;
        }

        cpu.Data[0x5b] = bank;
        SetUInt32(cpu, 20, cursor);
        cpu.SetUint16(30, pointer);
        cpu.PC = GdfsErasedRangeCompleteWord;
        return true;
    }

    public static bool TrySkipGdfsSectorHeaderMismatchRun(Cpu cpu)
    {
        return TrySkipGdfsSectorHeaderMismatchRun(
            cpu,
            long.MaxValue,
            out _);
    }

    /// <summary>
    /// Executes a proven GDFS-sector scan prefix atomically. The caller must
    /// supply the cycle room before its next emulated event; on success this
    /// method reproduces the firmware's architectural state and reports the
    /// number of AVR instructions it collapsed.
    /// </summary>
    public static bool TrySkipGdfsSectorHeaderMismatchRun(
        Cpu cpu,
        long maximumCycles,
        out int skippedInstructions)
    {
        skippedInstructions = 0;
        if (cpu.PC != GdfsSectorHeaderScanWord || cpu.Data[4] != 0)
        {
            return false;
        }

        var cursor = GetUInt32(cpu, 20);
        if (cursor < 0x400 || cursor >= 0x10000)
        {
            return false;
        }

        var keyLow = cpu.Data[6];
        var keyHigh = cpu.Data[7];
        long skippedCycles = 0;
        while (cursor < 0x10000)
        {
            // A mismatch takes 36 cycles, which is the longest possible
            // path for this candidate. Do not touch firmware-visible memory
            // unless the entire iteration fits before the next event.
            if (maximumCycles - skippedCycles < GdfsSectorHeaderMismatchCycles)
            {
                break;
            }

            // The firmware forms a 24-bit data-space address by adding the
            // unit in r24 to the logical GDFS base bank 0xd8, then adding the
            // within-unit cursor. Each nine-byte sector header stores the
            // lookup key in bytes +1 and +2.
            var address = (int)((((uint)cpu.Data[24] << 16) + cursor + 0xd80000u) & 0xffffffu);
            var low = cpu.ReadData(address + 1);
            var high = cpu.ReadData(address + 2);

            cpu.Data[0] = low;
            cpu.Data[1] = high;
            cpu.Data[16] = (byte)address;
            cpu.Data[17] = (byte)(address >> 8);
            cpu.Data[18] = (byte)(address >> 16);
            cpu.SetUint16(30, address);
            cpu.Data[0x5b] = (byte)(address >> 16);
            SetUInt32(cpu, 20, cursor);

            if (low == keyLow && high == keyHigh)
            {
                // A match is seven cycles shorter than a mismatch, so its
                // bound is already guaranteed by the conservative check.
                // Resume at the entry-type load. The firmware retains all
                // match handling, output writes, and status decisions.
                SetGdfsSectorHeaderCompareStatus(
                    cpu,
                    new(low, high, keyLow, keyHigh));
                cpu.PC = GdfsSectorHeaderMatchWord;
                cpu.Cycles += skippedCycles + GdfsSectorHeaderMatchCycles;
                skippedInstructions += 26;
                return true;
            }

            SetGdfsSectorHeaderAdvanceStatus(cpu, cursor);
            cursor += 9;
            skippedCycles += GdfsSectorHeaderMismatchCycles;
            skippedInstructions += 31;
        }

        if (skippedInstructions == 0)
        {
            return false;
        }

        // Leave the cursor, flags, and PC as they are after the last native
        // mismatch. The next firmware iteration performs its own bound and
        // not-found handling.
        SetUInt32(cpu, 20, cursor);
        cpu.Cycles += skippedCycles;
        return true;
    }

    public static bool TrySkipGdfsEntryHeaderMismatchRun(Cpu cpu)
    {
        if (cpu.PC != GdfsEntryHeaderScanWord)
        {
            return false;
        }

        var cursor = GetUInt32(cpu, 24);
        if (cursor >= 0x10000)
        {
            return false;
        }

        var originalCursor = cursor;
        var stack = cpu.GetUint16(28) | cpu.Data[0x5a] << 16;
        if (stack + 5 >= cpu.Data.Length)
        {
            return false;
        }
        var targetLow = cpu.ReadData(stack + 4);
        var targetHigh = cpu.ReadData(stack + 5);

        while (CanInspectNextGdfsEntry(cursor) &&
            !IsTargetGdfsEntry(cpu, cursor + 9, targetLow, targetHigh))
        {
            cursor += 9;
        }

        return AdvanceGdfsEntryCursor(cpu, originalCursor, cursor);
    }

    static bool CanInspectNextGdfsEntry(uint cursor) => cursor + 18 < 0x10000;

    static bool IsTargetGdfsEntry(
        Cpu cpu,
        uint cursor,
        byte targetLow,
        byte targetHigh)
    {
        // Native code converts the physical unit byte in r4 to the logical
        // GDFS data-space bank by subtracting 0x28.
        var bank = unchecked((byte)(cpu.Data[4] - 0x28));
        var address = bank << 16 | (int)cursor;
        var type = cpu.ReadData(address);
        if (type < 0x10)
        {
            // The native low-type branch rereads the type before looping.
            _ = cpu.ReadData(address);
            return false;
        }

        var keyLow = cpu.ReadData(address + 1);
        var keyHigh = cpu.ReadData(address + 2);
        return keyLow == targetLow && keyHigh == targetHigh;
    }

    public static bool TrySkipGdfsLinkedRecordMismatchRun(Cpu cpu)
    {
        if (cpu.PC != GdfsLinkedRecordScanWord || cpu.Data[3] != 0)
        {
            return false;
        }

        var target = GetUInt24(cpu, 0);
        var current = GetUInt24(cpu, 20);
        if (target == 0 || current == 0)
        {
            return false;
        }

        // The native loop rereads the two-byte target key for every node.
        // Preserve that behavior while skipping only nodes whose +6/+7 key
        // differs; every matching node resumes in firmware before any of its
        // signal-specific handling.
        var seen = new HashSet<int>();
        while (current != 0 &&
            !ProcessLinkedRecordNode(cpu, target, ref current, seen))
        {
        }

        SetUInt24(cpu, 20, current);
        return true;
    }

    static bool ProcessLinkedRecordNode(
        Cpu cpu,
        int target,
        ref int current,
        HashSet<int> seen)
    {
        if (!seen.Add(current))
        {
            return true;
        }

        var targetLow = ReadBankedOffset(cpu, target, 3);
        var targetHigh = ReadBankedOffset(cpu, target, 4);
        var currentLow = ReadBankedOffset(cpu, current, 6);
        var currentHigh = ReadBankedOffset(cpu, current, 7);
        cpu.Data[24] = targetLow;
        cpu.Data[25] = targetHigh;
        cpu.Data[26] = currentLow;
        cpu.Data[27] = currentHigh;
        PublishLinkedNode(cpu, current, 20);
        if (targetLow == currentLow && targetHigh == currentHigh)
        {
            cpu.PC = GdfsLinkedRecordMatchWord;
            return true;
        }

        current = ReadBankedOffset(cpu, current, 3) |
            ReadBankedOffset(cpu, current, 4) << 8 |
            ReadBankedOffset(cpu, current, 5) << 16;
        return false;
    }

    public static bool TrySkipGdfsLinkedExtentMismatchRun(Cpu cpu)
    {
        if (cpu.PC != GdfsLinkedExtentScanWord)
        {
            return false;
        }

        var target = cpu.Data[26] | cpu.Data[27] << 8 | cpu.Data[25] << 16;
        var current = GetUInt24(cpu, 16);
        if (target == 0 || current == 0)
        {
            return false;
        }

        var seen = new HashSet<int>();
        while (current != 0 &&
            !ProcessLinkedExtentNode(cpu, target, ref current, seen))
        {
        }

        SetUInt24(cpu, 16, current);
        return true;
    }

    static bool ProcessLinkedExtentNode(
        Cpu cpu,
        int target,
        ref int current,
        HashSet<int> seen)
    {
        if (!seen.Add(current))
        {
            return true;
        }

        var targetLow = ReadBankedOffset(cpu, target, 3);
        var targetHigh = ReadBankedOffset(cpu, target, 4);
        var currentLow = ReadBankedOffset(cpu, current, 6);
        var currentHigh = ReadBankedOffset(cpu, current, 7);
        cpu.Data[22] = targetLow;
        cpu.Data[23] = targetHigh;
        cpu.Data[0] = currentLow;
        cpu.Data[1] = currentHigh;
        PublishLinkedNode(cpu, current, 16);
        if (targetLow == currentLow && targetHigh == currentHigh)
        {
            cpu.PC = GdfsLinkedExtentMatchWord;
            return true;
        }

        current = ReadBankedOffset(cpu, current, 0) |
            ReadBankedOffset(cpu, current, 1) << 8 |
            ReadBankedOffset(cpu, current, 2) << 16;
        return false;
    }

    static void PublishLinkedNode(Cpu cpu, int current, int register)
    {
        cpu.SetUint16(30, current);
        cpu.Data[0x5b] = (byte)(current >> 16);
        SetUInt24(cpu, register, current);
    }

    static byte ReadBankedOffset(Cpu cpu, int pointer, int offset) =>
        cpu.ReadData(pointer & 0xff0000 | (pointer + offset) & 0xffff);

    static bool AdvanceGdfsEntryCursor(Cpu cpu, uint originalCursor, uint cursor)
    {
        if (cursor == originalCursor)
        {
            return false;
        }
        SetUInt32(cpu, 24, cursor);
        return true;
    }

    static void SetGdfsSectorHeaderCompareStatus(
        Cpu cpu,
        AsicGdfsComparison comparison)
    {
        var low = comparison.Low;
        var high = comparison.High;
        var keyLow = comparison.KeyLow;
        var keyHigh = comparison.KeyHigh;
        int lowResult = low - keyLow;
        int status = cpu.SREG & 0xc0;
        status |= lowResult != 0 ? 0 : 2;
        status |= (lowResult & 0x80) != 0 ? 4 : 0;
        status |= ((low ^ keyLow) & (low ^ lowResult) & 0x80) != 0 ? 8 : 0;
        status |= (((status >> 2) & 1) ^ ((status >> 3) & 1)) != 0
            ? 0x10
            : 0;
        status |= keyLow > low ? 1 : 0;
        status |= (1 & ((~low & keyLow) | (keyLow & lowResult) |
            (lowResult & ~low))) != 0
                ? 0x20
                : 0;

        int highResult = high - keyHigh - (status & 1);
        status = (status & 0xc0) |
            (highResult == 0 && ((status >> 1) & 1) != 0 ? 2 : 0) |
            (keyHigh + (status & 1) > high ? 1 : 0);
        status |= (highResult & 0x80) != 0 ? 4 : 0;
        status |= ((high ^ keyHigh) & (high ^ highResult) & 0x80) != 0
            ? 8
            : 0;
        status |= (((status >> 2) & 1) ^ ((status >> 3) & 1)) != 0
            ? 0x10
            : 0;
        status |= (1 & ((~high & keyHigh) | (keyHigh & highResult) |
            (highResult & ~high))) != 0
                ? 0x20
                : 0;
        cpu.SetStatusRegister((byte)status);
    }

    static void SetGdfsSectorHeaderAdvanceStatus(Cpu cpu, uint cursor)
    {
        int status = GetSubtractStatus(
            cpu.SREG,
            (byte)cursor,
            0xf7);
        status = GetSubtractWithCarryStatus(
            status,
            (byte)(cursor >> 8),
            0xff);
        status = GetSubtractWithCarryStatus(
            status,
            (byte)(cursor >> 16),
            0xff);
        status = GetSubtractWithCarryStatus(
            status,
            (byte)(cursor >> 24),
            0xff);
        cpu.SetStatusRegister((byte)status);
    }

    static int GetSubtractStatus(int initialStatus, int left, int right)
    {
        int result = left - right;
        int status = initialStatus & 0xc0;
        status |= result != 0 ? 0 : 2;
        status |= (result & 0x80) != 0 ? 4 : 0;
        status |= ((left ^ right) & (left ^ result) & 0x80) != 0 ? 8 : 0;
        status |= (((status >> 2) & 1) ^ ((status >> 3) & 1)) != 0
            ? 0x10
            : 0;
        status |= right > left ? 1 : 0;
        status |= (1 & ((~left & right) | (right & result) |
            (result & ~left))) != 0
                ? 0x20
                : 0;
        return status;
    }

    static int GetSubtractWithCarryStatus(int initialStatus, int left, int right)
    {
        int carry = initialStatus & 1;
        int result = left - right - carry;
        int status = (initialStatus & 0xc0) |
            (result == 0 && ((initialStatus >> 1) & 1) != 0 ? 2 : 0) |
            (right + carry > left ? 1 : 0);
        status |= (result & 0x80) != 0 ? 4 : 0;
        status |= ((left ^ right) & (left ^ result) & 0x80) != 0 ? 8 : 0;
        status |= (((status >> 2) & 1) ^ ((status >> 3) & 1)) != 0
            ? 0x10
            : 0;
        status |= (1 & ((~left & right) | (right & result) |
            (result & ~left))) != 0
                ? 0x20
                : 0;
        return status;
    }

    static int GetUInt24(Cpu cpu, int register) =>
        cpu.Data[register] |
        cpu.Data[register + 1] << 8 |
        cpu.Data[register + 2] << 16;

    static void SetUInt24(Cpu cpu, int register, int value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
        cpu.Data[register + 2] = (byte)(value >> 16);
    }

    static uint GetUInt32(Cpu cpu, int register) =>
        (uint)(cpu.Data[register] |
            cpu.Data[register + 1] << 8 |
            cpu.Data[register + 2] << 16 |
            cpu.Data[register + 3] << 24);

    static void SetUInt32(Cpu cpu, int register, uint value)
    {
        cpu.Data[register] = (byte)value;
        cpu.Data[register + 1] = (byte)(value >> 8);
        cpu.Data[register + 2] = (byte)(value >> 16);
        cpu.Data[register + 3] = (byte)(value >> 24);
    }
}
