// SPDX-License-Identifier: Zlib

namespace Arm7Core;

internal sealed class Arm7TdmiBoothState
{
    public required bool Signed { get; init; }
    public required bool LongResult { get; init; }
    public required ulong Multiplicand { get; init; }
    public required bool AdderCarryIn { get; init; }
    public required Arm7TdmiCsaResult Csa { get; set; }
    public required ulong Multiplier { get; set; }
    public required ulong AccumulatorShiftRegister { get; set; }
    public required UInt128 PartialSum { get; set; }
    public required UInt128 PartialCarry { get; set; }
    public int Iterations { get; set; }
}
