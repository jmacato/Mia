// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct ArmHalfwordTransfer
{
    public bool PreIndex { get; init; }
    public bool AddOffset { get; init; }
    public bool WriteBack { get; init; }
    public bool Load { get; init; }
    public int BaseRegister { get; init; }
    public int DestinationRegister { get; init; }
    public bool Signed { get; init; }
    public bool Halfword { get; init; }
    public uint Offset { get; init; }
}
