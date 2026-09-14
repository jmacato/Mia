// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly ref struct GsmLapdmUplinkFrame(
    byte sapi,
    byte sendSequence,
    byte receiveSequence,
    bool moreData,
    bool pollFinal,
    ReadOnlySpan<byte> information)
{
    public byte Sapi { get; } = sapi;
    public byte SendSequence { get; } = sendSequence;
    public byte ReceiveSequence { get; } = receiveSequence;
    public bool MoreData { get; } = moreData;
    public bool PollFinal { get; } = pollFinal;
    public ReadOnlySpan<byte> Information { get; } = information;
}
