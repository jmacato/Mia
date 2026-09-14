// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct ArmBlockTransferState(
    uint Address,
    ArmAccess Access,
    bool FirstTransfer);
