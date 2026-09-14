using Arm7Core;
using Xunit;

namespace Arm7Core.Tests;

public sealed class Arm7TdmiTests
{
    [Theory]
    [InlineData(0x01000100u, 0x00008001u, 0xFFFF8001u)]
    [InlineData(0x01000101u, 0x0000807Fu, 0xFFFFFF80u)]
    public void ArmLdrshSignExtendsAlignedAndUnalignedValues(
        uint address,
        uint halfword,
        uint expected)
    {
        const uint ldrshR2FromR1 = 0xE1D120F0;
        const uint nop = 0xE1A00000;
        Arm7TdmiTestsTestBus bus = new() { HalfwordReadValue = halfword };
        Arm7Tdmi cpu = new(bus);

        cpu.ForceStatus(Arm7Tdmi.ModeSys);
        cpu.SetGpr(1, address);
        cpu.SetGpr(15, 0x01000008);
        cpu.PrimePipeline(
            ldrshR2FromR1,
            nop,
            ArmAccess.Code | ArmAccess.Sequential);

        cpu.Step();

        Assert.Equal(expected, cpu.GetGpr(2));
    }

    [Fact]
    public void ArmMsrWhenDisablingFiqPreventsImmediateNestedFiq()
    {
        const uint msrCpsrFieldsR1 = 0xE129F001;
        const uint nop = 0xE1A00000;
        const uint fiqAndIrqDisabled = 0xC0;
        Arm7TdmiTestsTestBus bus = new();
        Arm7Tdmi cpu = new(bus);

        cpu.ForceStatus(Arm7Tdmi.ModeSys);
        cpu.SetGpr(1, Arm7Tdmi.ModeFiq | fiqAndIrqDisabled);
        cpu.SetGpr(15, 0x01000008);
        cpu.PrimePipeline(
            msrCpsrFieldsR1,
            nop,
            ArmAccess.Code | ArmAccess.Sequential);

        cpu.Step();
        cpu.FiqLine = true;
        cpu.Step();

        Assert.Equal(Arm7Tdmi.ModeFiq | fiqAndIrqDisabled, cpu.CpsrValue);
        Assert.Equal(0x01000010u, cpu.GetGpr(15));
        Assert.Equal(0u, cpu.GetGpr(14));
    }

    [Fact]
    public void ArmLdmUserRegistersDoesNotCorruptFiqStackOnFollowingInstruction()
    {
        const uint ldmUserR0FromSp = 0xE89D0001;
        const uint addSpFour = 0xE28DD004;
        const uint fiqStack = 0x00100100;
        const uint systemStack = 0x00100200;
        Arm7TdmiTestsTestBus bus = new();
        Arm7Tdmi cpu = new(bus);

        cpu.ForceStatus(Arm7Tdmi.ModeFiq | 0xC0);
        cpu.SetGpr(13, fiqStack);
        cpu.SetBanked(ArmBank.None, 5, systemStack);
        cpu.SetGpr(15, 0x01000008);
        cpu.PrimePipeline(
            ldmUserR0FromSp,
            addSpFour,
            ArmAccess.Code | ArmAccess.Sequential);

        cpu.Step();
        cpu.Step();

        Assert.Equal(fiqStack + 4, cpu.GetGpr(13));
        Assert.Equal(systemStack, cpu.GetBanked(ArmBank.None, 5));
    }

    [Fact]
    public void ThumbMultiplyReproducesArm7BoothCarry()
    {
        const uint thumbSvcIrqDisabled = Arm7Tdmi.ModeSvc | 0xA0;
        const uint thumbMulR2R0 = 0x4342;
        Arm7TdmiTestsTestBus bus = new();
        Arm7Tdmi cpu = new(bus);

        cpu.ForceStatus(thumbSvcIrqDisabled | 0xE0000000);
        cpu.SetGpr(0, 0x85EA0FEC);
        cpu.SetGpr(2, 0xBCE4CB58);
        cpu.SetGpr(15, 0x01000004);
        cpu.PrimePipeline(
            thumbMulR2R0,
            0x46C0,
            ArmAccess.Code | ArmAccess.None);

        cpu.Step();

        Assert.Equal(0x21459D20u, cpu.GetGpr(2));
        Assert.Equal(0x20000000u, cpu.CpsrValue & 0xF0000000u);
    }
}
