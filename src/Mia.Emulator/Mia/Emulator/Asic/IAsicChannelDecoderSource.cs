// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal interface IAsicChannelDecoderSource
{
    AsicChannelDecoderResult DecodeSch(ReadOnlyMemory<byte> input);

    AsicChannelDecoderResult DecodeControlChannel(
        ReadOnlyMemory<byte> input) => AsicChannelDecoderResult.Failure;

    AsicChannelDecoderResult DecodeControlChannel(
        AsicControlChannelDecodeRequest request) =>
        DecodeControlChannel(request.Input);
}
