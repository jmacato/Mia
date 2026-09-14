// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Arm7Core;

internal enum Arm7TdmiArmOperation : byte
{
    Undefined,
    DataProcessing,
    Multiply,
    MultiplyLong,
    Swap,
    BranchExchange,
    HalfwordTransfer,
    SingleTransfer,
    BlockTransfer,
    Branch,
    CoprocessorDataTransfer,
    CoprocessorDataOperation,
    CoprocessorRegisterTransfer,
    SoftwareInterrupt,
    Mrs,
    Msr,
}
