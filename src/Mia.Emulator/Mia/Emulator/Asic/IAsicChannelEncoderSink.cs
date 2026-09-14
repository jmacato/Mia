// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal interface IAsicChannelEncoderSink
{
    void Encode(ReadOnlyMemory<byte> input);

    void Encode(AsicChannelEncoderRequest request) => Encode(request.Input);
}
