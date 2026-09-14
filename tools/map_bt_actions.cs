#!/usr/bin/env dotnet

// SPDX-License-Identifier: MIT

const uint imageBase = 0x01000000;
const uint actionTable = 0x010adb9c;
const int actionCount = 0x145;

string firmwarePath = args.FirstOrDefault() ??
    "/private/tmp/mia-r8a015-modem.bin";
byte[] firmware = File.ReadAllBytes(firmwarePath);

ushort Read16(uint address)
{
    int offset = checked((int)(address - imageBase));
    return BitConverter.ToUInt16(firmware, offset);
}

uint FollowUnconditionalBranches(uint address)
{
    var visited = new HashSet<uint>();
    while (visited.Add(address))
    {
        ushort instruction = Read16(address);
        if ((instruction & 0xf800) != 0xe000)
        {
            return address;
        }

        int displacement = instruction & 0x07ff;
        if ((displacement & 0x0400) != 0)
        {
            displacement |= ~0x07ff;
        }
        address = unchecked(address + 4u + (uint)(displacement << 1));
    }

    return address;
}

for (int action = 0; action < actionCount; action++)
{
    uint tableEntry = actionTable + (uint)(action * sizeof(ushort));
    uint entryPoint = actionTable + Read16(tableEntry);
    uint resolvedEntryPoint = FollowUnconditionalBranches(entryPoint);
    Console.WriteLine(
        $"{action:x4} table=0x{tableEntry:x8} " +
        $"entry=0x{entryPoint:x8} resolved=0x{resolvedEntryPoint:x8}");
}
