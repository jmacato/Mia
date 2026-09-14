// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Mia.Desktop.Presentation;

internal sealed class DesktopMainViewHost : IMainViewHost
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }

    public void ReportError(string operation, Exception exception) =>
        Console.Error.WriteLine($"{operation} failed: {exception}");

    public void ReloadForRestart() => throw new NotSupportedException(
        "Desktop sessions restart in process.");

    public IMainViewAudioOutput CreateAudioOutput(int sampleRate) =>
        new OpenAlPcmSink(sampleRate);

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

    public async Task<bool> SaveFileAsync(
        string suggestedName,
        string mediaType,
        ReadOnlyMemory<byte> data)
    {
        _ = mediaType;
        IStorageProvider? storage = GetStorageProvider();
        if (storage is null)
        {
            return false;
        }

        IStorageFile? file = await storage.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                SuggestedFileName = Path.GetFileName(suggestedName),
            }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }

        Stream output = await file.OpenWriteAsync().ConfigureAwait(true);
        await using (output.ConfigureAwait(true))
        {
            output.SetLength(0);
            await output.WriteAsync(data).ConfigureAwait(true);
        }

        return true;
    }

    static IStorageProvider? GetStorageProvider() =>
        (Application.Current?.ApplicationLifetime as
            IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;
}
