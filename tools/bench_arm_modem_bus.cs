#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using System.Diagnostics;
using System.Buffers.Binary;
using Arm7Core;
using Mia.Emulator;

var iterations = args.Length > 0 && long.TryParse(args[0], out var parsed)
    ? parsed
    : 100_000_000;
var firmware = new byte[ArmModemFlashProfile.StM36Dr216C.Size];
for (var offset = 0; offset < firmware.Length; offset += 2)
{
    firmware[offset] = 0xc0;
    firmware[offset + 1] = 0x46; // Thumb MOV r8, r8
}
var wrapper = args.Contains("--wrapper", StringComparer.Ordinal);
var attachBluetooth = args.Contains("--bluetooth", StringComparer.Ordinal);
if (attachBluetooth && !wrapper)
{
    throw new ArgumentException("--bluetooth requires --wrapper.");
}
ArmModem? modem = null;
ArmModemBluetoothPeripheral? bluetooth = null;
ArmModemBus bus;
Arm7Tdmi cpu;
if (wrapper)
{
    var bih = new byte[ArmModemImage.HeaderLength + firmware.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(bih, ArmModemFlash.BaseAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(bih.AsSpan(4), (uint)firmware.Length);
    firmware.CopyTo(bih, ArmModemImage.HeaderLength);
    modem = new ArmModem(
        ArmModemImage.ParseBih(bih),
        ArmModemFlashProfile.StM36Dr216C);
    bus = modem.Bus;
    cpu = modem.Cpu;
    if (attachBluetooth)
    {
        bluetooth = new ArmModemBluetoothPeripheral(modem);
    }
}
else
{
    bus = new ArmModemBus(firmware, ArmModemFlashProfile.StM36Dr216C);
    cpu = new Arm7Tdmi(bus);
}

const int chunkInstructions = 500_000;
void ResetPipeline()
{
    cpu.ForceStatus(Arm7Tdmi.ModeSys | 0x20);
    cpu.SetGpr(15, ArmModemFlash.BaseAddress + 4);
    cpu.PrimePipeline(0x46c0, 0x46c0, ArmAccess.Code | ArmAccess.Sequential);
}

ResetPipeline();
for (var iteration = 0; iteration < 1_000_000; iteration++)
{
    if (modem is null)
    {
        cpu.Step();
    }
    else
    {
        modem.Step();
    }
}

ResetPipeline();
var stopwatch = Stopwatch.StartNew();
for (long completed = 0; completed < iterations;)
{
    var chunk = (int)Math.Min(chunkInstructions, iterations - completed);
    for (var iteration = 0; iteration < chunk; iteration++)
    {
        if (modem is null)
        {
            cpu.Step();
        }
        else
        {
            modem.Step();
        }
    }
    completed += chunk;
    ResetPipeline();
}
stopwatch.Stop();

Console.WriteLine($"Instructions: {iterations:n0}");
Console.WriteLine($"Elapsed:      {stopwatch.Elapsed.TotalSeconds:f3} s");
Console.WriteLine($"Throughput:   {iterations / stopwatch.Elapsed.TotalSeconds:n0} instructions/s");
Console.WriteLine($"Bus cycles:   {bus.Cycles:n0}");
bluetooth?.Dispose();
