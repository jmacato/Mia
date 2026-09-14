// SPDX-License-Identifier: MIT

namespace Arm7Core;

internal readonly record struct Arm7TdmiDataProcessingResult(
    uint Value,
    bool Arithmetic,
    bool Carry,
    bool Overflow);
