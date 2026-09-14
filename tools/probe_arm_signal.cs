#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

if (args.Length < 1 ||
    !ushort.TryParse(
        args[0],
        System.Globalization.NumberStyles.HexNumber,
        null,
        out ushort tracedSignal))
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/probe_arm_signal.cs -- HEX_SIGNAL [INSTRUCTIONS]");
    return 2;
}

long instructionLimit =
    args.Length > 1 && long.TryParse(args[1], out long parsedLimit)
        ? parsedLimit
        : 100_000_000;

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes("images/T68i_Full_GDFS.compact.raw"),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    powerPressedInitially: true,
    powerKeyReleaseCycle: MiaMachine.DefaultPowerKeyReleaseCycle,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

var trace = new List<string>();
int matches = 0;
int remaining = 0;
machine.Modem!.InstructionExecuting += modem =>
{
    uint pc = modem.CurrentInstructionAddress;
    if (remaining-- > 0 && trace.Count < 4_000)
    {
        trace.Add(
            $"post-receive cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
            $"r0=0x{modem.Cpu.GetGpr(0):x8} " +
            $"r1=0x{modem.Cpu.GetGpr(1):x8} " +
            $"r2=0x{modem.Cpu.GetGpr(2):x8} " +
            $"r3=0x{modem.Cpu.GetGpr(3):x8} " +
            $"r4=0x{modem.Cpu.GetGpr(4):x8} " +
            $"r7=0x{modem.Cpu.GetGpr(7):x8} " +
            $"lr=0x{modem.Cpu.GetGpr(14):x8}");
    }

    if (pc is not (0x01001e7c or 0x01001e94 or 0x01001dda))
    {
        return;
    }

    uint signalAddress = modem.Cpu.GetGpr(0);
    byte[] signal = Snapshot(modem.Bus, signalAddress, 128);
    if (signal.Length < sizeof(ushort) ||
        BitConverter.ToUInt16(signal, 0) != tracedSignal)
    {
        return;
    }

    matches++;
    uint currentTask = BitConverter.ToUInt32(
        modem.Bus.SnapshotInternal(0x0000285c, sizeof(uint)));
    byte[] task = Snapshot(modem.Bus, currentTask, 96);
    trace.Add(
        $"receive cycle={modem.Cycles:n0} pc=0x{pc:x8} " +
        $"signal=0x{signalAddress:x8}:{Convert.ToHexString(signal)} " +
        $"task=0x{currentTask:x8}:{Convert.ToHexString(task)} " +
        $"lr=0x{modem.Cpu.GetGpr(14):x8}");
    remaining = 256;
};

while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems((int)Math.Min(
        262_144,
        instructionLimit - machine.ExecutedInstructions));
}

Console.WriteLine(
    $"instructions={machine.ExecutedInstructions:n0} " +
    $"modem-instructions={machine.Modem.Instructions:n0} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason} matches={matches:n0}");
foreach (string line in trace)
{
    Console.WriteLine(line);
}

return 0;

static byte[] Snapshot(ArmModemBus bus, uint address, int length)
{
    if (address < ArmModemBus.InternalRamSize - length)
    {
        return bus.SnapshotInternal(address, length);
    }
    if (address >= ArmModemBus.ExternalRamBase &&
        address < ArmModemBus.ExternalRamBase +
            ArmModemBus.ExternalRamMirrorSpan - length)
    {
        return bus.SnapshotExternal(address, length);
    }
    return [];
}
