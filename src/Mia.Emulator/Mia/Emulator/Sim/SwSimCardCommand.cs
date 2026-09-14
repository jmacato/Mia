// SPDX-License-Identifier: BSD-3-Clause

namespace Mia.Emulator.Sim;

internal readonly ref struct SwSimCardCommand(
    byte p1,
    byte p2,
    byte p3,
    ReadOnlySpan<byte> data,
    int procedureCount)
{
    public byte P1 { get; } = p1;

    public byte P2 { get; } = p2;

    public byte P3 { get; } = p3;

    public ReadOnlySpan<byte> Data { get; } = data;

    public int ProcedureCount { get; } = procedureCount;
}
