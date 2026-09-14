// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

/// <summary>
/// One object exchanged with the native handset through a recovered OBEX
/// transport. The byte memory is owned by the snapshot.
/// </summary>
internal readonly record struct MiaTransferObject(
    string Name,
    string MediaType,
    ReadOnlyMemory<byte> Data)
{
    public MiaTransferObject Snapshot() => new(
        Name,
        MediaType,
        Data.ToArray());
}
