// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Arm7Core;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class ArmModemTests
{
    const uint Entry = ArmModemFlash.BaseAddress;

    [Fact]
    public void RunsFromBihLoadAddressUntilFirmwareEntersInterruptibleSleep()
    {
        byte[] payload = new byte[20];
        WriteWord(payload, 0, 0xe59f1008); // ldr r1, [pc, #8]
        WriteWord(payload, 4, 0xe3a00007); // mov r0, #7
        WriteWord(payload, 8, 0xe5c10000); // strb r0, [r1]
        WriteWord(payload, 12, 0xe1a00000); // nop
        WriteWord(payload, 16, 0x00800900);
        var modem = CreateModem(payload);

        modem.Step();
        modem.Step();
        modem.Step();

        Assert.False(modem.IsStopped);
        Assert.True(modem.IsSleeping);
        Assert.Equal(3, modem.Instructions);
        Assert.Equal(7, modem.Bus.PowerMode);
    }

    [Fact]
    public void DispatchesRecoveredMaskRomServiceThroughItsFirmwareAbi()
    {
        byte[] payload = new byte[20];
        WriteWord(payload, 0, 0xe59f3000); // ldr r3, [pc]
        WriteWord(payload, 4, 0xe12fff13); // bx r3
        WriteWord(payload, 8, ArmModemRomServices.ByteFillAddress | 1);
        WriteWord(payload, 12, 0xe1a00000); // return target
        var modem = CreateModem(payload);
        modem.Cpu.SetGpr(0, 0x40);
        modem.Cpu.SetGpr(1, 0xab);
        modem.Cpu.SetGpr(2, 1);
        modem.Cpu.SetGpr(14, Entry + 12);

        modem.Step();
        modem.Step();

        Assert.False(modem.IsStopped);
        Assert.Equal(2, modem.Instructions);
        Assert.Equal(1, modem.RomServices.CallCount);
        Assert.Equal([0xab], modem.Bus.SnapshotInternal(0x40, 1));
        Assert.Equal(Entry + 12, modem.CurrentInstructionAddress);
    }

    [Fact]
    public void DirectRomDispatchMatchesFetchTrapWithoutAllocatingPerCall()
    {
        byte[] payload = new byte[20];
        WriteWord(payload, 0, 0xe59f3000); // ldr r3, [pc]
        WriteWord(payload, 4, 0xe12fff13); // bx r3
        WriteWord(payload, 8, ArmModemRomServices.ByteFillAddress | 1);
        var direct = CreateModem(payload);
        var trapped = CreateModem(payload);
        trapped.Cpu.SetCodeFetchHandler(0, 0, _ => false);
        foreach (var modem in new[] { direct, trapped })
        {
            modem.Cpu.SetGpr(0, 0x40);
            modem.Cpu.SetGpr(1, 0xab);
            modem.Cpu.SetGpr(2, 1);
            modem.Cpu.SetGpr(14, Entry + 4);
            modem.Step();
            modem.Step();
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 5_000; index++)
        {
            direct.Step();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        for (var index = 0; index < 5_000; index++)
        {
            trapped.Step();
        }

        Assert.False(direct.IsStopped, direct.StopReason);
        Assert.False(trapped.IsStopped, trapped.StopReason);
        Assert.Equal(trapped.Cycles, direct.Cycles);
        Assert.Equal(trapped.Instructions, direct.Instructions);
        Assert.Equal(trapped.RomServices.CallCount, direct.RomServices.CallCount);
        Assert.Equal(trapped.Cpu.CpsrValue, direct.Cpu.CpsrValue);
        for (var register = 0; register < 16; register++)
        {
            Assert.Equal(trapped.Cpu.GetGpr(register), direct.Cpu.GetGpr(register));
        }
        Assert.Equal(trapped.Bus.SnapshotInternal(0x40, 1), direct.Bus.SnapshotInternal(0x40, 1));
        Assert.True(allocated < 1_024, $"ROM dispatch allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void RecoveredMaskRomCrc16MatchesTheIrdaFcsResidue()
    {
        byte[] payload = new byte[20];
        WriteWord(payload, 0, 0xe59f3000); // ldr r3, [pc]
        WriteWord(payload, 4, 0xe12fff13); // bx r3
        WriteWord(payload, 8, ArmModemRomServices.Crc16Address | 1);
        WriteWord(payload, 12, 0xe1a00000); // return target
        var modem = CreateModem(payload);
        byte[] message = "123456789"u8.ToArray();
        for (var index = 0; index < message.Length; index++)
        {
            modem.Bus.WriteByte(
                0x40u + (uint)index,
                message[index],
                ArmAccess.None);
        }
        modem.Cpu.SetGpr(0, 0x40);
        modem.Cpu.SetGpr(1, (uint)message.Length);
        modem.Cpu.SetGpr(2, 0xffff);
        modem.Cpu.SetGpr(14, Entry + 12);

        modem.Step();
        modem.Step();

        Assert.False(modem.IsStopped);
        Assert.Equal(0x6f91u, modem.Cpu.GetGpr(0));
        Assert.Equal(Entry + 12, modem.CurrentInstructionAddress);
    }

    [Fact]
    public void StopsAtUnknownMmioWithoutInventingAValue()
    {
        byte[] payload = new byte[12];
        WriteWord(payload, 0, 0xe5d10000); // ldrb r0, [r1]
        WriteWord(payload, 4, 0xe1a00000); // nop
        WriteWord(payload, 8, 0xe1a00000); // nop
        var modem = CreateModem(payload);
        modem.Cpu.SetGpr(1, 0x0080ffff);

        modem.Step();

        Assert.Equal(ArmModemStopKind.UnmappedBusAccess, modem.StopKind);
        Assert.Equal(0, modem.Instructions);
        Assert.Contains("pc=0x01000000", modem.StopReason, StringComparison.Ordinal);
        Assert.Contains("address=0x0080ffff", modem.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnUnrecoveredBihLoadAddress()
    {
        byte[] bih = BuildBih([], 0x02000000);
        ArmModemImage image = ArmModemImage.ParseBih(bih);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => new ArmModem(image, ArmModemFlashProfile.StM36Dr216C));

        Assert.Contains("load address 0x02000000 is unsupported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PeripheralEventsRunAtTheirCycleWithoutHostPolling()
    {
        var bus = new ArmModemBus([], ArmModemFlashProfile.StM36Dr216C);
        var observedCycles = new List<long>();
        using IDisposable cancelled = bus.SchedulePeripheralEvent(
            75,
            observedCycles.Add);
        cancelled.Dispose();
        bus.SchedulePeripheralEvent(50, observedCycles.Add);
        bus.SchedulePeripheralEvent(100, observedCycles.Add);

        bus.IdleUntilCycle(100);

        Assert.Equal([50L, 100L], observedCycles);
        Assert.Equal(100, bus.Cycles);
    }

    [Fact]
    public void TimerPollLoopReturnsToTheSameCpuStateAfterTwentyThreeCycles()
    {
        var modem = CreateTimerPollingModem();
        uint[] registers = Enumerable.Range(0, 16).Select(modem.Cpu.GetGpr).ToArray();
        long start = modem.Cycles;

        for (int i = 0; i < 11; i++) modem.Step();

        Assert.False(modem.IsStopped, modem.StopReason);
        Assert.Equal(23, modem.Cycles - start);
        Assert.Equal(registers, Enumerable.Range(0, 16).Select(modem.Cpu.GetGpr));
        Assert.Equal(0x600000f3u, modem.Cpu.CpsrValue);
        Assert.Equal(0x48ddu, modem.Cpu.GetPipelineOpcode(0));
        Assert.Equal(0x7800u, modem.Cpu.GetPipelineOpcode(1));
        Assert.Equal(ArmAccess.Code | ArmAccess.Sequential, modem.Cpu.PipelineAccess);
    }

    [Theory]
    [InlineData(2, 24)]
    [InlineData(2, 190)]
    [InlineData(180, 500)]
    [InlineData(202, 2_000)]
    [InlineData(2, 2_000)]
    public void TimerPollFastForwardMatchesEveryRegisterAndCounterBoundary(int start, int deadline)
    {
        var fast = CreateTimerPollingModem();
        var native = CreateTimerPollingModem();
        fast.Bus.IdleUntilCycle(start);
        native.Bus.IdleUntilCycle(start);

        fast.RunUntilCycle(deadline);
        while (native.Cycles < deadline && !native.IsStopped) native.Step();

        AssertModemsEqual(native, fast);
    }

    [Fact]
    public void TimerPollFastForwardStopsBeforePeripheralEvents()
    {
        var fast = CreateTimerPollingModem();
        var native = CreateTimerPollingModem();
        foreach (var modem in new[] { fast, native })
        {
            modem.Bus.SchedulePeripheralEvent(100,
                _ => modem.Bus.WriteByte(0x0080095c, 0x10, ArmAccess.None));
        }

        Assert.True(fast.TryFastForwardTimerPoll(1_000));
        Assert.InRange(fast.Cycles, 25, 99);
        fast.RunUntilCycle(500);
        while (native.Cycles < 500 && !native.IsStopped) native.Step();

        AssertModemsEqual(native, fast);
        Assert.Equal(0x010e2dfcu, fast.CurrentInstructionAddress);
    }

    [Theory]
    [InlineData("general observer")]
    [InlineData("instruction observer")]
    [InlineData("MMIO observer")]
    [InlineData("changed flag")]
    [InlineData("changed register")]
    [InlineData("changed pipeline")]
    [InlineData("changed flash")]
    [InlineData("FIQ")]
    public void TimerPollFastForwardRequiresUnobservedUnchangedExecution(string change)
    {
        var modem = CreateTimerPollingModem(change == "changed flash");
        switch (change)
        {
            case "general observer": modem.InstructionExecuting += _ => { }; break;
            case "instruction observer": modem.ObserveInstruction(0x010e2dfa, _ => { }); break;
            case "MMIO observer": modem.Bus.MmioAccessed += _ => { }; break;
            case "changed flag": modem.Bus.WriteByte(0x0080095c, 0x10, ArmAccess.None); break;
            case "changed register": modem.Cpu.SetGpr(4, 1); break;
            case "changed pipeline": modem.Cpu.PrimePipeline(0x46c0, 0x7800, ArmAccess.Code | ArmAccess.Sequential); break;
            case "FIQ": modem.Cpu.FiqLine = true; break;
        }
        long cycles = modem.Cycles;

        Assert.False(modem.TryFastForwardTimerPoll(1_000));
        Assert.Equal(cycles, modem.Cycles);
        Assert.Equal(0, modem.Instructions);
    }

    static void AssertModemsEqual(ArmModem expected, ArmModem actual)
    {
        Assert.False(expected.IsStopped, expected.StopReason);
        Assert.False(actual.IsStopped, actual.StopReason);
        Assert.Equal(expected.Cycles, actual.Cycles);
        Assert.Equal(expected.Instructions, actual.Instructions);
        Assert.Equal(expected.Cpu.CpsrValue, actual.Cpu.CpsrValue);
        Assert.Equal(Enumerable.Range(0, 16).Select(expected.Cpu.GetGpr),
            Enumerable.Range(0, 16).Select(actual.Cpu.GetGpr));
        Assert.Equal(expected.Cpu.GetPipelineOpcode(0), actual.Cpu.GetPipelineOpcode(0));
        Assert.Equal(expected.Cpu.GetPipelineOpcode(1), actual.Cpu.GetPipelineOpcode(1));
        Assert.Equal(expected.Cpu.PipelineAccess, actual.Cpu.PipelineAccess);
    }

    static ArmModem CreateTimerPollingModem(bool changedFlash = false)
    {
        byte[] payload = new byte[0xe314c];
        Convert.FromHexString("DD4800781023184010D1DA480068041CAC420AD0251C701E061C06D1D7A0AFF0F5F8D9A0AFF0F2F800E0E9E7FEE7")
            .CopyTo(payload, 0xe2dd0);
        if (changedFlash) payload[0xe2dd4] = 17;
        WriteWord(payload, 0xe3144, 0x00800700);
        WriteWord(payload, 0xe3148, 0x0080095c);
        var modem = CreateModem(payload);
        modem.Cpu.ForceStatus(0x600000f3);
        modem.Cpu.SetGpr(3, 16);
        modem.Cpu.SetGpr(6, 100);
        modem.Cpu.SetGpr(15, 0x010e2dd4);
        modem.Cpu.PrimePipeline(0x48dd, 0x7800, ArmAccess.Code | ArmAccess.Sequential);
        return modem;
    }

    static ArmModem CreateModem(byte[] payload) =>
        new(
            ArmModemImage.ParseBih(BuildBih(payload, Entry)),
            ArmModemFlashProfile.StM36Dr216C);

    static byte[] BuildBih(byte[] payload, uint loadAddress)
    {
        byte[] bih = new byte[ArmModemImage.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bih, loadAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(bih.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bih, ArmModemImage.HeaderLength);
        return bih;
    }

    static void WriteWord(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
}
