// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct ArmSingleTransfer
{
    public bool PreIndex { get; init; }
    public bool AddOffset { get; init; }
    public bool ByteTransfer { get; init; }
    public bool WriteBack { get; init; }
    public bool Load { get; init; }
    public int BaseRegister { get; init; }
    public int DestinationRegister { get; init; }
    public uint Offset { get; init; }
}
