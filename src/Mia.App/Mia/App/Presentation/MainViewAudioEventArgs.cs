// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewAudioEventArgs(ReadOnlyMemory<short> samples) : EventArgs
{
    public ReadOnlyMemory<short> Samples { get; } = samples;
}
