// SPDX-License-Identifier: BSD-3-Clause

namespace Mia.Emulator.Sim;

internal readonly record struct SwSimCardApduResult(byte[] Data, byte Sw1, byte Sw2, int ProcedureLength)
{
    public static SwSimCardApduResult Status(byte sw1, byte sw2) => new([], sw1, sw2, -1);

    public static SwSimCardApduResult Response(byte[] data, byte sw1, byte sw2) => new(data, sw1, sw2, -1);

    public static SwSimCardApduResult Procedure(int length) => new([], 0, 0, length);
}
