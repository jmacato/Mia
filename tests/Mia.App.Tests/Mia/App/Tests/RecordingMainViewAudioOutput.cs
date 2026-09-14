// SPDX-License-Identifier: MIT

using Mia.App.Presentation;

namespace Mia.App.Tests;

internal sealed class RecordingMainViewAudioOutput(int sampleRate) :
    IMainViewAudioOutput
{
    public int SampleRate { get; } = sampleRate;
    public List<short> Samples { get; } = [];
    public int DisposeCount { get; private set; }
    public bool AcceptsWorkerThread { get; set; }
    public int LastWriteThread { get; private set; }

    public void PushSamples(ReadOnlySpan<short> samples)
    {
        LastWriteThread = Environment.CurrentManagedThreadId;
        foreach (short sample in samples)
        {
            Samples.Add(sample);
        }
    }

    public void Dispose() => DisposeCount++;
}
