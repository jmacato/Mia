using Arm7Core;

namespace Arm7Core.Tests;

internal sealed class Arm7TdmiTestsTestBus : IArm7Bus
{
    public uint HalfwordReadValue { get; init; }

    public uint ReadWord(uint address, ArmAccess access) => 0;

    public uint ReadHalf(uint address, ArmAccess access) => HalfwordReadValue;

    public uint ReadByte(uint address, ArmAccess access) => 0;

    public void WriteWord(uint address, uint value, ArmAccess access)
    {
    }

    public void WriteHalf(uint address, ushort value, ArmAccess access)
    {
    }

    public void WriteByte(uint address, byte value, ArmAccess access)
    {
    }

    public void Idle()
    {
    }
}
