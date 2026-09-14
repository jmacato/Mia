// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.JavaScript;

namespace Mia.Browser.Audio;

internal static partial class BrowserAudioInterop
{
    public const string ModuleName = "mia-audio";

    [JSImport("start", ModuleName)]
    internal static partial bool Start(int sourceSampleRate, int ringAddress, int ringCapacity);

    [JSImport("pushSamples", ModuleName)]
    internal static partial void PushSamples(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> samples);

    [JSImport("stop", ModuleName)]
    internal static partial void Stop();
}
