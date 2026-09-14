// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public interface IMainViewHost
{
    void Post(Action action);

    void ReportError(string operation, Exception exception);

    void ReloadForRestart();

    IMainViewAudioOutput CreateAudioOutput(int sampleRate);

    Task<MainViewHostFile?> PickFileAsync(string title);

    Task<bool> SaveFileAsync(
        string suggestedName,
        string mediaType,
        ReadOnlyMemory<byte> data);
}
