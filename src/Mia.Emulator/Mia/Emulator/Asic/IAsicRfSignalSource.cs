// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal interface IAsicRfSignalSource
{
    ushort ReadRawSample(
        byte adcSelector,
        AsicRfTransaction transaction,
        AsicTimeGeneratorActionExecution action);
}
