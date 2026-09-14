// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicNoSignalChannelDecoderSource : IAsicChannelDecoderSource
{
    public static AsicNoSignalChannelDecoderSource Instance { get; } = new();

    AsicNoSignalChannelDecoderSource()
    {
    }

    public AsicChannelDecoderResult DecodeSch(ReadOnlyMemory<byte> input) =>
        AsicChannelDecoderResult.Failure;
}
