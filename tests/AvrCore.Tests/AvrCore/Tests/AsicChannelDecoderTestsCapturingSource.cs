// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class AsicChannelDecoderTestsCapturingSource(
    byte[] result,
    byte[]? controlChannelResult = null,
    long? lateControlChannelResultCycles = null) : IAsicChannelDecoderSource
{
    public byte[]? Input { get; private set; }

    public byte[]? ControlChannelInput { get; private set; }

    public byte? ControlChannelFirmwareState { get; private set; }

    public AsicChannelDecoderResult DecodeSch(ReadOnlyMemory<byte> input)
    {
        Input = input.ToArray();
        return new(true, result);
    }

    public AsicChannelDecoderResult DecodeControlChannel(
        ReadOnlyMemory<byte> input)
    {
        ControlChannelInput = input.ToArray();
        return controlChannelResult is null
            ? AsicChannelDecoderResult.Failure
            : new(
                true,
                controlChannelResult,
                lateControlChannelResultCycles);
    }

    public AsicChannelDecoderResult DecodeControlChannel(
        AsicControlChannelDecodeRequest request)
    {
        ControlChannelFirmwareState = request.FirmwareState;
        return DecodeControlChannel(request.Input);
    }
}
