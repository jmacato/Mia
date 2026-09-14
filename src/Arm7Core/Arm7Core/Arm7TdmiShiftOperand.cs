// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct Arm7TdmiShiftOperand(
    uint Value,
    int Type,
    uint Amount,
    bool RegisterSpecified);
