// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicNoSignalAdcSampleSource : IAsicAdcSampleSource
{
    public static AsicNoSignalAdcSampleSource Instance { get; } = new();

    AsicNoSignalAdcSampleSource()
    {
    }

    public ushort ReadRawSample(
        byte adcSelector,
        AsicTimeGeneratorActionExecution action) => 0;
}
