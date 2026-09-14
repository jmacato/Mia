// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.JavaScript;

namespace Mia.Browser.Interop;

internal static partial class BrowserPersistenceInterop
{
    public const string ModuleName = "mia-persistence";

    [JSImport("loadText", ModuleName)]
    private static partial string? LoadText(string key);

    [JSImport("saveText", ModuleName)]
    private static partial void SaveText(string key, string value);

    public static MiaPersistenceSnapshot? Load(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? text = LoadText(key);
        cancellationToken.ThrowIfCancellationRequested();
        return text is null ? null : MiaPersistence.Deserialize(text);
    }

    public static void Save(
        string key,
        MiaPersistenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveText(key, MiaPersistence.Serialize(snapshot));
        cancellationToken.ThrowIfCancellationRequested();
    }
}
