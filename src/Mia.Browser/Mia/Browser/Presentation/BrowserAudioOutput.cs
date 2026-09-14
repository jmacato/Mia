// SPDX-License-Identifier: MIT

namespace Mia.Browser.Presentation;

internal sealed class BrowserAudioOutput : IMainViewAudioOutput
{
    readonly BrowserAudioSink _sink;

    public bool AcceptsWorkerThread => true;

    public BrowserAudioOutput(int sampleRate)
    {
        _sink = new BrowserAudioSink(sampleRate);
    }

    public void PushSamples(ReadOnlySpan<short> samples) =>
        _sink.PushSamples(samples);

    public void Dispose() => _sink.Dispose();
}
