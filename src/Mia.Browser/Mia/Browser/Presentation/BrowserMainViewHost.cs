// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Mia.Browser.Presentation;

internal sealed class BrowserMainViewHost : IMainViewHost
{
    public void Post(Action action) => BrowserDispatcher.Post(action);

    public void ReportError(string operation, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);
        Console.Error.WriteLine($"Mia browser {operation} failed: {error}");
    }

    public void ReloadForRestart() =>
        BrowserPageInterop.ReloadForEmulatorRestart();

    public IMainViewAudioOutput CreateAudioOutput(int sampleRate) =>
        new BrowserAudioOutput(sampleRate);

    public async Task<MainViewHostFile?> PickFileAsync(string title)
    {
        IStorageProvider? storage = GetStorageProvider();
        if (storage is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files =
            await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            }).ConfigureAwait(true);
        IStorageFile? file = files.Count == 0 ? null : files[0];
        if (file is null)
        {
            return null;
        }

        Stream input = await file.OpenReadAsync().ConfigureAwait(true);
        await using (input.ConfigureAwait(true))
        {
            using var output = new MemoryStream();
            await input.CopyToAsync(output).ConfigureAwait(true);
            return new MainViewHostFile(file.Name, output.ToArray());
        }
    }

    public Task<bool> SaveFileAsync(
        string suggestedName,
        string mediaType,
        ReadOnlyMemory<byte> data)
    {
        byte[] bytes = data.ToArray();
        BrowserFileDownloadInterop.DownloadTransferFile(
            Path.GetFileName(suggestedName),
            mediaType,
            bytes.AsSpan());
        return Task.FromResult(true);
    }

    static IStorageProvider? GetStorageProvider() =>
        (Application.Current?.ApplicationLifetime as
            ISingleViewApplicationLifetime)?.MainView is { } mainView
                ? TopLevel.GetTopLevel(mainView)?.StorageProvider
                : null;
}
