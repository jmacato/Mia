// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class AsicPhCommandControllerTestsRecordingEncoderSink : IAsicChannelEncoderSink
{
    public byte[] Input { get; private set; } = [];

    public byte? FirmwareState { get; private set; }

    public void Encode(ReadOnlyMemory<byte> input) =>
        Input = input.ToArray();

    public void Encode(AsicChannelEncoderRequest request)
    {
        FirmwareState = request.FirmwareState;
        Encode(request.Input);
    }
}
