#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// Capture every known physical-output transition around a firmware-owned
// ringtone. This deliberately observes only latches and tone MMIO; it does
// not assign an LED meaning to either one.

using Mia.Emulator;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel,
    liveGsm: new MiaLiveGsmOptions());

var events = new List<string>();
var gate = new object();
var ringtoneActive = false;
var registrations = 0;
var queued = false;

void Record(string text)
{
    lock (gate)
    {
        events.Add($"c={machine.Cycles,12} i={machine.ExecutedInstructions,12} {text}");
    }
}

machine.PowerPorts.OutputStateChanged += state => Record(
    $"power A3={state.PortA3:x2} A4={state.PortA4:x2} A5={state.PortA5:x2} " +
    $"A6={state.PortA6:x2} A7={state.PortA7:x2} A9={state.PortA9:x2} " +
    $"AA={state.PowerControl:x2} B0={state.PortB0:x2}");
machine.SecondaryPorts.OutputStateChanged += state => Record(
    $"secondary 40={state.Register40:x2} 48={state.Register48:x2} 80={state.Register80:x2}");
machine.SecondaryPorts.OpaqueRegisterWritten += (register, value) => Record(
    $"secondary opaque {register:x2}={value:x2}");
machine.PrimaryI2c.TransactionCompleted += (address, payload) =>
{
    if (address == MiaSecondaryPortController.WriteAddress &&
        payload.Length == 2 && payload[0] == 0x84)
    {
        Record($"secondary transaction 84={payload[1]:x2} " +
            $"completed-at-pc={machine.PrimaryI2c.LastWritePc:x6}");
    }
};
machine.ToneGenerator.StateChanged += state =>
{
    if (state.Control == 0x80)
    {
        ringtoneActive = true;
    }
    if (queued)
    {
        Record($"tone 840={state.Control:x2} 841={state.Width:x2} " +
            $"842={state.Reload:x2} 843={state.Unknown:x2}");
    }
};
machine.StatusIndicators.StateChanged += state => Record(
    $"current compatibility status green={state.NetworkGreen} blue={state.BluetoothBlue}");

while (!machine.IsStopped && machine.ExecutedInstructions < 320_000_000)
{
    machine.RunWorkItems(262_144);
    var live = machine.LiveGsm!;
    var status = live.GetStatus(machine);
    if (status.Registered)
    {
        registrations++;
    }
    if (!queued && status.Registered && registrations > 3)
    {
        if (!live.TryQueueIncomingCall("5551234", autoAnswer: false, out var result))
        {
            throw new InvalidOperationException(result);
        }
        queued = true;
        Record(result);
    }
    if (ringtoneActive && status.IncomingCallSetups > 0 &&
        machine.Cycles > 440_000_000)
    {
        break;
    }
}

lock (gate)
{
    foreach (var entry in events)
    {
        Console.WriteLine(entry);
    }
}
var finalStatus = machine.LiveGsm!.GetStatus(machine);
Console.WriteLine(
    $"END c={machine.Cycles} i={machine.ExecutedInstructions} " +
    $"registered={finalStatus.Registered} call-setups={finalStatus.IncomingCallSetups} " +
    $"ringtone-active={ringtoneActive} stopped={machine.IsStopped} reason={machine.StopReason}");
