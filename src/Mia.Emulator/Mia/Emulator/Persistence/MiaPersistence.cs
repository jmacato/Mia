// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;

namespace Mia.Emulator.Persistence;

internal static class MiaPersistence
{
    public static string CreateKey(ReadOnlySpan<byte> firmware)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(firmware, hash);
        return $"{firmware.Length:X8}-{Convert.ToHexString(hash)}";
    }

    public static string Serialize(MiaPersistenceSnapshot snapshot) =>
        JsonSerializer.Serialize(
            snapshot,
            MiaPersistenceJsonContext.Default.MiaPersistenceSnapshot);

    public static MiaPersistenceSnapshot? Deserialize(string text)
    {
        try
        {
            MiaPersistenceSnapshot? snapshot = JsonSerializer.Deserialize(
                text,
                MiaPersistenceJsonContext.Default.MiaPersistenceSnapshot);
            return snapshot?.Version == MiaPersistenceSnapshot.CurrentVersion
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
