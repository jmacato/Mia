// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class AsicAdcTestsFixedSampleSource(ushort value) : IAsicAdcSampleSource
{
    public ushort ReadRawSample(
        byte adcSelector,
        AsicTimeGeneratorActionExecution action) => value;
}
