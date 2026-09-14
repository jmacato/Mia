// SPDX-License-Identifier: MIT

using Arm7Core;

namespace AvrCore.Tests;

internal sealed class ArmModemRomServicesTestsTestBus : IArm7Bus
{
    readonly byte[] _data = new byte[0x200];

    public uint ReadWord(uint address, ArmAccess access) =>
        (uint)(_data[address] | _data[address + 1] << 8 |
            _data[address + 2] << 16 | _data[address + 3] << 24);

    public uint ReadHalf(uint address, ArmAccess access) =>
        (uint)(_data[address] | _data[address + 1] << 8);

    public uint ReadByte(uint address, ArmAccess access) => _data[address];

    public void WriteWord(uint address, uint value, ArmAccess access)
    {
        for (var offset = 0; offset < 4; offset++)
        {
            _data[address + offset] = (byte)(value >> (offset * 8));
        }
    }

    public void WriteHalf(uint address, ushort value, ArmAccess access)
    {
        _data[address] = (byte)value;
        _data[address + 1] = (byte)(value >> 8);
    }

    public void WriteByte(uint address, byte value, ArmAccess access) =>
        _data[address] = value;

    public void Idle()
    {
    }

    public void WriteBytes(uint address, byte[] values) =>
        values.CopyTo(_data, address);

    public byte[] ReadBytes(uint address, int count) =>
        _data.AsSpan((int)address, count).ToArray();
}
