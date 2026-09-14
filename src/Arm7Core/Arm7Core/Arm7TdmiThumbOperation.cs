// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Arm7Core;

internal enum Arm7TdmiThumbOperation : byte
{
    Undefined,
    MoveShiftedRegister,
    AddSubtract,
    ImmediateAlu,
    Alu,
    HighRegister,
    PcRelativeLoad,
    RegisterOffsetTransfer,
    SignedTransfer,
    ImmediateTransfer,
    ImmediateHalfwordTransfer,
    SpRelativeTransfer,
    LoadAddress,
    AddSpOffset,
    PushPop,
    MultipleTransfer,
    ConditionalBranch,
    SoftwareInterrupt,
    Branch,
    LongBranch,
}
