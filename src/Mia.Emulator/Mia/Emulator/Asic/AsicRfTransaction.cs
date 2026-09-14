// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicRfTransaction(
    byte Slot,
    ReadOnlyMemory<byte> Bytes)
{
    public byte TuneLow => GetByte(7);

    public byte TuneHigh => GetByte(8);

    public bool HasDuplicatedTuneWord =>
        Bytes.Length == AsicRfFrontend.TransactionSize &&
        Bytes.Span[7] == Bytes.Span[9] &&
        Bytes.Span[8] == Bytes.Span[10];

    byte GetByte(int index) => Bytes.Length == AsicRfFrontend.TransactionSize
        ? Bytes.Span[index]
        : throw new InvalidOperationException(
            $"RF transaction must contain {AsicRfFrontend.TransactionSize} bytes.");
}
