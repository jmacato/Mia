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
    powerPortSiliconRevision: 0xf4);

Run(160_000_000);
Print("before");
machine.PowerPorts.SetExternalPowerConnected(true);
Print("edge");
Run(40_000_000);
Print("after");

void Run(long instructionCount)
{
    long target = machine.ExecutedInstructions + instructionCount;
    while (!machine.IsStopped && machine.ExecutedInstructions < target)
    {
        machine.RunWorkItems(262_144);
    }
}

void Print(string stage) =>
    Console.WriteLine(
        $"stage={stage} instructions={machine.ExecutedInstructions:n0} " +
        $"mask=0x{machine.PowerPorts.InterruptMask:x2} " +
        $"interrupts={machine.PowerPorts.InterruptCount:n0} " +
        $"external={machine.PowerPorts.ExternalPowerConnected} " +
        $"edge-reads={machine.PowerPorts.OnOffStatusReadCount:n0} " +
        $"status-reads={machine.PowerPorts.PortStatusReadCount:n0} " +
        $"voltage={machine.PowerPorts.VoltageAdcReadCount:n0} " +
        $"current={machine.PowerPorts.ChargingCurrentAdcReadCount:n0} " +
        $"temperature={machine.PowerPorts.BatteryTemperatureAdcReadCount:n0} " +
        $"next-interrupt={machine.Cpu.NextInterrupt}");
