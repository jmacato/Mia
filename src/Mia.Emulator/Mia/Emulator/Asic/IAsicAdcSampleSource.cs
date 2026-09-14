// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal interface IAsicAdcSampleSource
{
    ushort ReadRawSample(
        byte adcSelector,
        AsicTimeGeneratorActionExecution action);
}
