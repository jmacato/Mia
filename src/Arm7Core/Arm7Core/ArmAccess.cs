// SPDX-License-Identifier: MIT

namespace Arm7Core;

[Flags]
public enum ArmAccess
{
    None = 0,
    Sequential = 1,
    Code = 2,
    Dma = 4,
    Lock = 8,
}
