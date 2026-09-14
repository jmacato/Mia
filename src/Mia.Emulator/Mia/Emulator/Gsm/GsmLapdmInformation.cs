// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly ref struct GsmLapdmInformation(
    byte sapi,
    ReadOnlySpan<byte> payload,
    byte sendSequence,
    byte receiveSequence,
    bool moreData = false)
{
    public byte Sapi { get; } = sapi;

    public ReadOnlySpan<byte> Payload { get; } = payload;

    public byte SendSequence { get; } = sendSequence;

    public byte ReceiveSequence { get; } = receiveSequence;

    public bool MoreData { get; } = moreData;
}
