#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

// Exercises the already-recovered Bluetooth DSP FIFO/IRQ boundary with a
// complete record, while recording every physical-output latch. The record is
// delivered to the real ARM BT_Ctrl decoder; no AVR or ARM task state is set.

using Mia.Emulator;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);
var modem = machine.Modem ?? throw new InvalidOperationException("ARM modem did not start.");

for (var address = 0; address <= byte.MaxValue; address++)
{
    modem.Bus.SetDspIndexedRegister((byte)address, 0);
}
modem.Bus.SetDspIndexedRegister(15, 0xd0);
modem.Bus.SetDspIndexedRegister(16, 0xd0);

var outputEvents = new List<string>();
var gate = new object();
void Record(string value)
{
    lock (gate)
    {
        outputEvents.Add(
            $"c={machine.Cycles,12} i={machine.ExecutedInstructions,12} {value}");
    }
}

machine.PowerPorts.OutputStateChanged += state => Record(
    $"power A3={state.PortA3:x2} A4={state.PortA4:x2} A5={state.PortA5:x2} " +
    $"A6={state.PortA6:x2} A7={state.PortA7:x2} A9={state.PortA9:x2} " +
    $"AA={state.PowerControl:x2} B0={state.PortB0:x2}");
machine.SecondaryPorts.OutputStateChanged += state => Record(
    $"secondary 40={state.Register40:x2} 48={state.Register48:x2} " +
    $"80={state.Register80:x2}");
machine.SecondaryPorts.OpaqueRegisterWritten += (register, value) => Record(
    $"secondary opaque {register:x2}={value:x2}");
modem.Bus.DspPacketTransmitted += packet => Record(
    $"BT DSP packet type={packet.Type:x2} payload={Convert.ToHexString(packet.Payload)}");
modem.Bus.DspTransferTransmitted += transfer => Record(
    $"BT DSP transfer control={transfer.Control:x2} kind={transfer.Kind:x} " +
    $"payload={Convert.ToHexString(transfer.Payload)} trailer={transfer.Trailer:x2}");

bool acknowledgeBluetoothTransfer = false;
modem.Bus.DspPacketTransmitted += packet =>
{
    if (acknowledgeBluetoothTransfer &&
        packet.Type == 0x10 && packet.Payload.Length == 1 &&
        packet.Payload[0] == 0x03)
    {
        Record("acknowledge BT DSP fixed-transfer readiness with status 08");
        modem.Bus.AssertDspStatus(0x08);
        acknowledgeBluetoothTransfer = false;
    }
};

// The ARM Bluetooth controller starts accepting C3F2 records around 2.0M
// modem cycles. Deliver it from the ARM worker itself: external injection
// after a work-item batch is not a firmware-visible IRQ boundary.
bool recordInjected = false;
modem.InstructionExecuting += arm =>
{
    if (!recordInjected && arm.Cycles >= 2_000_000)
    {
        Record("inject complete record-id=5 payload=0A + sixteen zero bytes");
        acknowledgeBluetoothTransfer = true;
        arm.Bus.QueueDspInboundPayload(
            [0x0a, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        recordInjected = true;
    }
};
Run(17_500_000);

lock (gate)
{
    foreach (var outputEvent in outputEvents)
    {
        Console.WriteLine(outputEvent);
    }
}
Console.WriteLine(
    $"END c={machine.Cycles} i={machine.ExecutedInstructions} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason}");

void Run(long count)
{
    var end = machine.ExecutedInstructions + count;
    while (!machine.IsStopped && machine.ExecutedInstructions < end)
    {
        machine.RunWorkItems((int)Math.Min(262_144, end - machine.ExecutedInstructions));
    }
    if (machine.IsStopped)
    {
        throw new InvalidOperationException(machine.StopReason);
    }
}
