#!/usr/bin/env dotnet
#:project ../src/Mia.Emulator/Mia.Emulator.csproj

using Mia.Emulator;

if (args.Length < 3 ||
    !long.TryParse(args[0], out var instructionLimit) ||
    !int.TryParse(args[2], System.Globalization.NumberStyles.HexNumber, null, out var address))
{
    Console.Error.WriteLine(
        "usage: dotnet run tools/probe_sim_status_writes.cs -- " +
        "INSTRUCTIONS GDFS STATUS_RECORD_HEX");
    return 2;
}

using var machine = new MiaMachine(
    File.ReadAllBytes("flat.bin"),
    File.ReadAllBytes(args[1]),
    File.ReadAllBytes("images/t68i_R8A015_125326_Modem.bih"),
    Convert.FromHexString("321A065432100654"),
    virtualSim: true,
    coreSchedulingMode: MiaCoreSchedulingMode.CoarseParallel);

machine.ExecutionObserver = new StatusWriteObserver(address, 32);
while (!machine.IsStopped && machine.ExecutedInstructions < instructionLimit)
{
    machine.RunWorkItems(262_144);
}

Console.WriteLine(
    $"END i={machine.ExecutedInstructions} c={machine.Cycles} " +
    $"stopped={machine.IsStopped} reason={machine.StopReason}");
return 0;

sealed class StatusWriteObserver : IMiaExecutionObserver
{
    readonly int _address;
    readonly byte[] _previous;
    int _previousPc;
    byte _previousProcess;
    long _previousCycle;
    bool _initialized;
    bool _queryActive;

    public StatusWriteObserver(int address, int length)
    {
        _address = address;
        _previous = new byte[length];
    }

    public string? BeforeWorkItem(MiaMachine machine)
    {
        var cpu = machine.Cpu;
        if (cpu.PC == 0x025a5d && cpu.Data[20] == 0x5e)
        {
            _queryActive = true;
            Console.WriteLine(
                $"QUERY i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"process={cpu.ReadData(0xf606):x2} " +
                $"regs16-25={Convert.ToHexString(cpu.Data.AsSpan(16, 10))}");
        }
        if (_queryActive && cpu.ReadData(0xf606) == 0x42 && cpu.PC == 0x3f009c)
        {
            var destination = ReadRegisterPointer(cpu, 16);
            var source = ReadRegisterPointer(cpu, 20);
            var logicalY = cpu.Data[28] | cpu.Data[29] << 8 | cpu.Data[0x5a] << 16;
            var physicalY = cpu.TranslateDataAddress(logicalY);
            var count = cpu.Data[physicalY] | cpu.Data[physicalY + 1] << 8;
            Console.WriteLine(
                $"MEMCPY i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"caller={_previousPc:x6} dest={destination:x6} source={source:x6} " +
                $"count={count} dest-bytes={ReadBytes(cpu, destination, count)} " +
                $"source-bytes={ReadBytes(cpu, source, count)}");
        }
        if (_queryActive && cpu.ReadData(0xf606) == 0x42 && cpu.PC == 0x01e3f2)
        {
            var source = ReadRegisterPointer(cpu, 16);
            var destination = ReadRegisterPointer(cpu, 20);
            var logicalY = cpu.Data[28] | cpu.Data[29] << 8 | cpu.Data[0x5a] << 16;
            var physicalY = cpu.TranslateDataAddress(logicalY);
            var maximum = cpu.Data[physicalY + 4];
            Console.WriteLine(
                $"DECODE i={machine.ExecutedInstructions} c={machine.Cycles} " +
                $"caller={_previousPc:x6} source={source:x6} dest={destination:x6} " +
                $"maximum={maximum} source-bytes={ReadBytes(cpu, source, 48)} " +
                $"dest-bytes={ReadBytes(cpu, destination, maximum)}");
        }
        var current = cpu.Data.AsSpan(_address, _previous.Length);
        if (!_initialized)
        {
            current.CopyTo(_previous);
            _initialized = true;
        }
        else if (!current.SequenceEqual(_previous))
        {
            for (var offset = 0; offset < _previous.Length; offset++)
            {
                if (_previous[offset] == current[offset])
                {
                    continue;
                }
                Console.WriteLine(
                    $"WRITE i={machine.ExecutedInstructions} c={machine.Cycles} " +
                    $"writer-c={_previousCycle} process={_previousProcess:x2} " +
                    $"pc={_previousPc:x6} address={_address + offset:x6} " +
                    $"old={_previous[offset]:x2} new={current[offset]:x2} " +
                    $"record={Convert.ToHexString(current)}");
            }
            current.CopyTo(_previous);
        }

        _previousPc = cpu.PC;
        _previousProcess = cpu.ReadData(0xf606);
        _previousCycle = machine.Cycles;
        return null;
    }

    public void AfterRomDispatch(
        MiaMachine machine,
        int entry,
        AsicRomDispatchResult result)
    {
    }

    static int ReadRegisterPointer(AvrCore.Cpu cpu, int register) =>
        cpu.Data[register] | cpu.Data[register + 1] << 8 | cpu.Data[register + 2] << 16;

    static string ReadBytes(AvrCore.Cpu cpu, int logicalAddress, int count)
    {
        var physicalAddress = cpu.TranslateDataAddress(logicalAddress);
        if ((uint)physicalAddress >= (uint)cpu.Data.Length)
        {
            return "invalid";
        }
        return Convert.ToHexString(cpu.Data.AsSpan(
            physicalAddress,
            Math.Min(Math.Min(count, 64), cpu.Data.Length - physicalAddress)));
    }
}
