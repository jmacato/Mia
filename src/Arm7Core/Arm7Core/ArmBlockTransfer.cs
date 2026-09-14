// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct ArmBlockTransfer
{
    public bool PreIndex { get; init; }
    public bool Increment { get; init; }
    public bool UserOrRestore { get; init; }
    public bool WriteBack { get; init; }
    public bool Load { get; init; }
    public int BaseRegister { get; init; }
    public uint RegisterList { get; init; }
    public bool EmptyRegisterList { get; init; }
    public bool ProgramCounterInList { get; init; }
    public bool UseUserBank { get; init; }
    public int TransferCount { get; init; }
}
