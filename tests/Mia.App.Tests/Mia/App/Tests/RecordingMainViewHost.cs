// SPDX-License-Identifier: MIT

using Mia.App.Presentation;

namespace Mia.App.Tests;

internal sealed class RecordingMainViewHost(bool queuePosts = false) : IMainViewHost
{
    readonly Queue<Action> _posts = new();

    public MainViewHostFile? SelectedFile { get; init; }
    public bool SaveResult { get; init; }
    public List<(string Operation, Exception Error)> Errors { get; } = [];
    public List<string> PickTitles { get; } = [];
    public List<(string Name, string MediaType, ReadOnlyMemory<byte> Data)>
        SavedFiles
    { get; } = [];
    public int ReloadCount { get; private set; }
    public int Count => _posts.Count;
    public RecordingMainViewAudioOutput? AudioOutput { get; private set; }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (queuePosts)
        {
            _posts.Enqueue(action);
        }
        else
        {
            action();
        }
    }

    public void ReportError(string operation, Exception exception) =>
        Errors.Add((operation, exception));

    public void ReloadForRestart() => ReloadCount++;

    public IMainViewAudioOutput CreateAudioOutput(int sampleRate) =>
        AudioOutput = new RecordingMainViewAudioOutput(sampleRate);

    public Task<MainViewHostFile?> PickFileAsync(string title)
    {
        PickTitles.Add(title);
        return Task.FromResult(SelectedFile);
    }

    public Task<bool> SaveFileAsync(
        string suggestedName,
        string mediaType,
        ReadOnlyMemory<byte> data)
    {
        SavedFiles.Add((suggestedName, mediaType, data));
        return Task.FromResult(SaveResult);
    }

    public void Drain()
    {
        while (_posts.TryDequeue(out Action? action))
        {
            action();
        }
    }
}
