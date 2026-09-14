// SPDX-License-Identifier: MIT

using Mia.Desktop.Persistence;
using Mia.Emulator.Persistence;
using Xunit;

namespace Mia.App.Tests;

public sealed class FileMiaPersistenceStoreTests
{
    [Fact]
    public async Task LoadFallsBackToLegacyApplicationData()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"mia-persistence-tests-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "Mia", "persistence");
        string legacyDirectory = Path.Combine(root, "T68i", "persistence");
        const string key = "firmware";
        MiaPersistenceSnapshot snapshot = new(
            MiaPersistenceSnapshot.CurrentVersion,
            [new(0x7f10, 0x6f3c, [0x03, 0x06, 0x91])]);
        try
        {
            Directory.CreateDirectory(legacyDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(legacyDirectory, $"{key}.json"),
                MiaPersistence.Serialize(snapshot));
            var store = new FileMiaPersistenceStore(directory, legacyDirectory);

            MiaPersistenceSnapshot? restored = await store.LoadAsync(
                key,
                CancellationToken.None);

            Assert.NotNull(restored);
            Assert.Single(restored.SimFiles);
            await store.SaveAsync(key, restored, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(directory, $"{key}.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
