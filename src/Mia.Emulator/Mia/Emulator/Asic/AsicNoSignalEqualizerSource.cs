// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicNoSignalEqualizerSource : IAsicEqualizerSource
{
    public static AsicNoSignalEqualizerSource Instance { get; } = new();

    AsicNoSignalEqualizerSource()
    {
    }

    public AsicEqualizerResult Equalize(AsicEqualizerRequest request) =>
        AsicEqualizerResult.Invalid;
}
