// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public interface IMainViewAudioOutput : IDisposable
{
    bool AcceptsWorkerThread => false;
    void PushSamples(ReadOnlySpan<short> samples);
}
