// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicFchResult(
    bool Success,
    ReadOnlyMemory<byte> Bytes)
{
    public static AsicFchResult Failure { get; } =
        new(false, ReadOnlyMemory<byte>.Empty);
}
