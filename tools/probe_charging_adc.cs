#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// SPDX-License-Identifier: MIT

using Mia.Emulator;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
    liveGsm: new MiaLiveGsmOptions(),
    externalPowerConnectedInitially: true);

machine.ExecutionObserver = new ChargingAdcObserver();

try
{
    while (!machine.IsStopped && machine.ExecutedInstructions < 130_000_000)
    {
        machine.RunWorkItems(262_144);
    }
}
catch (InvalidOperationException exception)
{
    Console.WriteLine($"boundary={exception.Message}");
}

Console.WriteLine(
    $"end instructions={machine.ExecutedInstructions:n0} cycles={machine.Cycles:n0} " +
    $"pc=0x{machine.Cpu.PC:x6} adc-control=0x{machine.PowerPorts.AdcControl:x2} " +
    $"external-power={machine.PowerPorts.ExternalPowerConnected}");

sealed class ChargingAdcObserver : IMiaExecutionObserver
{
    static readonly HashSet<int> WatchedPcs =
    [
        0x078fb8,
        0x0790a6,
        0x0790b1,
        0x0790d4,
        0x0790e2,
        0x07912e,
        0x079373,
        0x0794ff,
        0x07953f,
    ];

    public string? BeforeWorkItem(MiaMachine machine)
    {
        if (!WatchedPcs.Contains(machine.Cpu.PC))
        {
            return null;
        }

        int stackPointer = machine.Cpu.SP;
        int returnAddress =
            machine.Cpu.Data[stackPointer + 3] |
            machine.Cpu.Data[stackPointer + 2] << 8 |
            machine.Cpu.Data[stackPointer + 1] << 16;
        Console.WriteLine(
            $"i={machine.ExecutedInstructions:n0} pc=0x{machine.Cpu.PC:x6} " +
            $"sp=0x{stackPointer:x4} return=0x{returnAddress:x6} " +
            $"r16=0x{machine.Cpu.Data[16]:x2} r17=0x{machine.Cpu.Data[17]:x2} " +
            $"r20=0x{machine.Cpu.Data[20]:x2} a8=0x{machine.PowerPorts.AdcControl:x2} " +
            $"a4=0x{machine.PowerPorts.OutputState.PortA4:x2} " +
            $"power=0x{machine.PowerPorts.PowerControl:x2} " +
            $"cal=" +
            Convert.ToHexString(machine.Cpu.Data.AsSpan(0x0286aa, 0x20)));
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }
}
