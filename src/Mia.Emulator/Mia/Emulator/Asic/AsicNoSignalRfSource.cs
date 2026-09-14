// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicNoSignalRfSource : IAsicRfSignalSource
{
    public static AsicNoSignalRfSource Instance { get; } = new();

    AsicNoSignalRfSource()
    {
    }

    public ushort ReadRawSample(
        byte adcSelector,
        AsicRfTransaction transaction,
        AsicTimeGeneratorActionExecution action) => 0;
}
