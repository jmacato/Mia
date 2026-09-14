#!/usr/bin/env dotnet

// SPDX-License-Identifier: MIT

const uint ImageBase = 0x01000000;
const uint StateIndexAddress = 0x0119763c;
const uint TransitionTableAddress = 0x01197740;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/dump_bt_state_tables.cs -- MODEM.bin STATE...");
    return 2;
}

byte[] firmware = File.ReadAllBytes(args[0]);

int FileOffset(uint address) => checked((int)(address - ImageBase));

uint Read32(uint address) =>
    BitConverter.ToUInt32(firmware, FileOffset(address));

ushort Read16(uint address) =>
    BitConverter.ToUInt16(firmware, FileOffset(address));

foreach (string stateText in args.Skip(1))
{
    int state = Convert.ToInt32(stateText, 0x10);
    if (state <= 0)
    {
        throw new ArgumentOutOfRangeException(
            nameof(args),
            "Runtime Bluetooth states are one-based.");
    }

    // 010c19c2 indexes the end offset with the one-based runtime state and
    // obtains the start offset from the preceding word at 010c19e2.
    uint firstEntry = Read32(
        StateIndexAddress + checked((uint)((state - 1) * 4)));
    uint nextEntry = Read32(
        StateIndexAddress + checked((uint)(state * 4)));
    Console.WriteLine(
        $"state=0x{state:x2} entries=0x{firstEntry:x}..0x{nextEntry:x} " +
        $"address=0x{TransitionTableAddress + firstEntry * 4:x8}");
    for (uint entry = firstEntry; entry < nextEntry; entry++)
    {
        uint address = TransitionTableAddress + entry * 4;
        Console.WriteLine(
            $"  signal=0x{Read16(address):x4} " +
            $"action=0x{Read16(address + 2):x4} " +
            $"address=0x{address:x8}");
    }
}

return 0;
