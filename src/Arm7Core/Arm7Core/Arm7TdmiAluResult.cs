// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct Arm7TdmiAluResult(
    uint Value,
    bool Carry,
    bool Overflow);
