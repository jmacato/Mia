// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicChannelDecoderResult(
    bool Success,
    ReadOnlyMemory<byte> Bytes,
    long? LateResultCompletionCycles = null)
{
    public static AsicChannelDecoderResult Failure { get; } =
        new(false, ReadOnlyMemory<byte>.Empty);
}
