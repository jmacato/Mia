// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Arm7Core;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemLinkBridgeTests
{
    [Fact]
    public void CarriesBytesOnlyThroughBothFirmwareVisibleFifos()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        var modem = CreateModem();
        using var bridge = new ArmModemLinkBridge(channels, modem);

        modem.Bus.WriteByte(0x00800b14, 3, ArmAccess.None);
        cpu.WriteData(0x0915, 0x41);

        Assert.Equal(1, bridge.AsicToModemByteCount);
        Assert.Equal(1u, modem.Bus.ReadByte(0x00800b1c, ArmAccess.None));
        Assert.Equal(0x41u, modem.Bus.ReadByte(0x00800b10, ArmAccess.None));

        modem.Bus.WriteByte(0x00800b00, 0x4c, ArmAccess.None);

        for (var cycle = 0; cycle < ArmModemBus.Uart1CharacterCycles; cycle++)
        {
            modem.Bus.Idle();
        }

        Assert.Equal(1, bridge.ModemToAsicByteCount);
        Assert.Equal(1, channels.GetReceiveQueueLength(1));
        Assert.Equal(1, cpu.ReadData(0x0916));

        cpu.WriteData(0x091a, 1);

        Assert.Equal(1, cpu.ReadData(0x0916));
        Assert.Equal(0x4c, cpu.ReadData(0x0915));
    }

    [Fact]
    public void IgnoresTheAsicChannelThatIsNotWiredToModemUart1()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        var modem = CreateModem();
        using var bridge = new ArmModemLinkBridge(channels, modem);
        modem.Bus.WriteByte(0x00800b14, 3, ArmAccess.None);

        cpu.WriteData(0x0905, 0xaa);

        Assert.Equal(0, bridge.AsicToModemByteCount);
        Assert.Equal(0, modem.Bus.Uart1ReceivedCount);
    }

    [Fact]
    public void CycleLockedDeliveryOccursAtSerializedArrivalCycles()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var channels = new AsicByteChannels(cpu, clock);
        var modem = CreateModem();
        using var bridge = new ArmModemLinkBridge(
            channels,
            modem,
            asicClock: clock);
        modem.Bus.WriteByte(0x00800b14, 3, ArmAccess.None);

        cpu.WriteData(0x0915, 0x41);
        modem.Bus.IdleUntilCycle(
            AsicByteChannels.LinkTransmitCharacterCycles - 1);
        Assert.Equal(0, modem.Bus.Uart1ReceivedCount);

        modem.Bus.IdleUntilCycle(AsicByteChannels.LinkTransmitCharacterCycles);
        Assert.Equal(1, modem.Bus.Uart1ReceivedCount);

        modem.Bus.WriteByte(0x00800b00, 0x4c, ArmAccess.None);
        long modemArrival = modem.Bus.Cycles + ArmModemBus.Uart1CharacterCycles;
        bridge.CommitModemSchedules();
        clock.AdvanceTo(modemArrival - 1);
        Assert.Equal(0, channels.GetReceiveQueueLength(1));

        clock.AdvanceTo(modemArrival);
        Assert.Equal(1, channels.GetReceiveQueueLength(1));
        Assert.Equal(0x4c, cpu.ReadData(0x0915));
    }

    [Fact]
    public void CycleLockedOverdueDeliveryCommitsAtTheAsicBarrier()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var clock = new MiaSystemClock();
        var channels = new AsicByteChannels(cpu, clock);
        var modem = CreateModem();
        using var bridge = new ArmModemLinkBridge(
            channels,
            modem,
            asicClock: clock);

        modem.Bus.WriteByte(0x00800b00, 0x4c, ArmAccess.None);
        long modemArrival = modem.Bus.Cycles + ArmModemBus.Uart1CharacterCycles;
        clock.AdvanceTo(modemArrival);

        bridge.CommitModemSchedules();

        Assert.Equal(1, channels.GetReceiveQueueLength(1));
        Assert.Equal(0x4c, cpu.ReadData(0x0915));
    }

    [Fact]
    public void BufferedDeliveryCrossesIntoAsicOnlyWhenOwnerThreadDrainsIt()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        var modem = CreateModem();
        using var bridge = new ArmModemLinkBridge(
            channels,
            modem,
            bufferCoreEffects: true);

        modem.Bus.WriteByte(0x00800b00, 0x4c, ArmAccess.None);
        for (var cycle = 0; cycle < ArmModemBus.Uart1CharacterCycles; cycle++)
        {
            modem.Bus.Idle();
        }

        Assert.Equal(1, bridge.ModemToAsicByteCount);
        Assert.Equal(0, channels.GetReceiveQueueLength(1));

        bridge.CommitModemEffects();

        Assert.Equal(1, channels.GetReceiveQueueLength(1));
        Assert.Equal(0x4c, cpu.ReadData(0x0915));
    }

    [Fact]
    public void BufferedDeliveryCrossesIntoModemOnlyAtCoreBarrier()
    {
        var cpu = new Cpu(new byte[4], 0x1000);
        var channels = new AsicByteChannels(cpu, new MiaSystemClock());
        var modem = CreateModem();
        using var bridge = new ArmModemLinkBridge(
            channels,
            modem,
            bufferCoreEffects: true);
        modem.Bus.WriteByte(0x00800b14, 3, ArmAccess.None);

        cpu.WriteData(0x0915, 0x41);

        Assert.Equal(1, bridge.AsicToModemByteCount);
        Assert.Equal(0, modem.Bus.Uart1ReceivedCount);

        bridge.CommitAsicEffects();

        Assert.Equal(1, modem.Bus.Uart1ReceivedCount);
        Assert.Equal(1u, modem.Bus.ReadByte(0x00800b1c, ArmAccess.None));
        Assert.Equal(0x41u, modem.Bus.ReadByte(0x00800b10, ArmAccess.None));
    }

    static ArmModem CreateModem()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xe1a00000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0xe1a00000);
        byte[] bih = new byte[ArmModemImage.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bih, ArmModemFlash.BaseAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(bih.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bih, ArmModemImage.HeaderLength);
        return new ArmModem(
            ArmModemImage.ParseBih(bih),
            ArmModemFlashProfile.StM36Dr216C);
    }
}
