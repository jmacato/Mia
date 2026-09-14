// SPDX-License-Identifier: MIT

using Arm7Core;

namespace Mia.Emulator.Modem;

internal sealed class ArmModemRomServices
{
    public const uint Crc16Address = 0x00c00624;
    public const uint StringLengthAddress = 0x00c0067c;
    public const uint StringCopyAddress = 0x00c006c4;
    public const uint StringCompareAddress = 0x00c00708;
    public const uint ByteCopyAddress = 0x00c00790;
    public const uint ByteFillAddress = 0x00c00816;

    public long CallCount { get; private set; }

    internal static bool IsEntryPoint(uint address) => address is
        Crc16Address or StringLengthAddress or StringCopyAddress or
        StringCompareAddress or ByteCopyAddress or ByteFillAddress;

    public bool TryDispatch(Arm7Tdmi cpu, IArm7Bus bus, uint address)
    {
        switch (address)
        {
            case Crc16Address:
                Crc16(cpu, bus);
                break;
            case ByteFillAddress:
                Fill(cpu, bus);
                break;
            case StringLengthAddress:
                StringLength(cpu, bus);
                break;
            case ByteCopyAddress:
                Copy(cpu, bus);
                break;
            case StringCopyAddress:
                StringCopy(cpu, bus);
                break;
            case StringCompareAddress:
                StringCompare(cpu, bus);
                break;
            default:
                return false;
        }

        ReturnFromCall(cpu, bus);
        CallCount++;
        return true;
    }

    static void Fill(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint destination = cpu.GetGpr(0);
        byte value = (byte)cpu.GetGpr(1);
        uint length = cpu.GetGpr(2);
        for (uint offset = 0; offset < length; offset++)
        {
            bus.WriteByte(destination + offset, value, ArmAccess.None);
        }
    }

    static void Crc16(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint source = cpu.GetGpr(0);
        uint length = cpu.GetGpr(1);
        ushort fcs = (ushort)cpu.GetGpr(2);
        for (uint offset = 0; offset < length; offset++)
        {
            fcs ^= (byte)bus.ReadByte(
                source + offset,
                ArmAccess.None);
            for (var bit = 0; bit < 8; bit++)
            {
                fcs = (ushort)((fcs & 1) != 0
                    ? (fcs >> 1) ^ 0x8408
                    : fcs >> 1);
            }
        }

        cpu.SetGpr(0, fcs);
    }

    static void StringLength(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint source = cpu.GetGpr(0);
        uint length = 0;
        while (bus.ReadByte(source + length, ArmAccess.None) != 0)
        {
            length++;
        }

        cpu.SetGpr(0, length);
    }

    static void Copy(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint destination = cpu.GetGpr(0);
        uint source = cpu.GetGpr(1);
        uint length = cpu.GetGpr(2);
        for (uint offset = 0; offset < length; offset++)
        {
            byte value = (byte)bus.ReadByte(source + offset, ArmAccess.None);
            bus.WriteByte(destination + offset, value, ArmAccess.None);
        }

        // The image's fallback implementation advances all three working
        // registers, and callers may observe that ABI when ROM use is enabled.
        cpu.SetGpr(0, destination + length);
        cpu.SetGpr(1, source + length);
        cpu.SetGpr(2, 0);
    }

    static void StringCopy(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint destination = cpu.GetGpr(0);
        uint source = cpu.GetGpr(1);
        uint offset = 0;
        byte value;
        do
        {
            value = (byte)bus.ReadByte(source + offset, ArmAccess.None);
            bus.WriteByte(destination + offset, value, ArmAccess.None);
            offset++;
        }
        while (value != 0);

        cpu.SetGpr(0, destination);
    }

    static void StringCompare(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint left = cpu.GetGpr(0);
        uint right = cpu.GetGpr(1);
        uint offset = 0;
        while (true)
        {
            int leftByte = (byte)bus.ReadByte(left + offset, ArmAccess.None);
            int rightByte = (byte)bus.ReadByte(right + offset, ArmAccess.None);
            if (leftByte != rightByte)
            {
                cpu.SetGpr(0, unchecked((uint)(leftByte - rightByte)));
                return;
            }

            if (leftByte == 0)
            {
                cpu.SetGpr(0, 0);
                return;
            }

            offset++;
        }
    }

    static void ReturnFromCall(Arm7Tdmi cpu, IArm7Bus bus)
    {
        uint destination = cpu.GetGpr(14);
        bool thumb = (destination & 1) != 0;
        uint address = destination & ~1u;
        cpu.ForceStatus(thumb ? cpu.CpsrValue | 0x20u : cpu.CpsrValue & ~0x20u);
        if (thumb)
        {
            cpu.SetGpr(15, address + 4);
            cpu.PrimePipeline(
                bus.ReadHalf(address, ArmAccess.Code | ArmAccess.None),
                bus.ReadHalf(address + 2, ArmAccess.Code | ArmAccess.Sequential),
                ArmAccess.Code | ArmAccess.Sequential);
        }
        else
        {
            cpu.SetGpr(15, address + 8);
            cpu.PrimePipeline(
                bus.ReadWord(address, ArmAccess.Code | ArmAccess.None),
                bus.ReadWord(address + 4, ArmAccess.Code | ArmAccess.Sequential),
                ArmAccess.Code | ArmAccess.Sequential);
        }
    }
}
