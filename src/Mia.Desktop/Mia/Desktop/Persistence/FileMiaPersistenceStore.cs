// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace Mia.Desktop.Persistence;

internal sealed class FileMiaPersistenceStore : IMiaPersistenceStore
{
    public static FileMiaPersistenceStore Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mia",
            "persistence"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "T68i",
            "persistence"));

    readonly string _directory;
    readonly string? _legacyDirectory;

    public FileMiaPersistenceStore(string directory)
        : this(directory, legacyDirectory: null)
    {
    }

    internal FileMiaPersistenceStore(string directory, string? legacyDirectory)
    {
        _directory = directory;
        _legacyDirectory = legacyDirectory;
    }

    public async ValueTask<MiaPersistenceSnapshot?> LoadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        string path = PathForKey(key);
        if (!File.Exists(path) && _legacyDirectory is not null)
        {
            path = Path.Combine(_legacyDirectory, $"{key}.json");
        }
        if (!File.Exists(path))
        {
            return null;
        }

        string text = await File.ReadAllTextAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        return MiaPersistence.Deserialize(text);
    }

    public async ValueTask SaveAsync(
        string key,
        MiaPersistenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        string path = PathForKey(key);
        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                MiaPersistence.Serialize(snapshot),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    string PathForKey(string key) => Path.Combine(_directory, $"{key}.json");
}
