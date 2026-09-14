// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct Arm7TdmiShifterOperand(
    uint Value,
    bool? Carry,
    int PipelinePcOffset);
