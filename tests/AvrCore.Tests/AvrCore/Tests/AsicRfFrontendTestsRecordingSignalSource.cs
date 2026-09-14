// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class AsicRfFrontendTestsRecordingSignalSource : IAsicRfSignalSource
{
    public AsicRfTransaction? Transaction { get; private set; }

    public byte AdcSelector { get; private set; }

    public ushort ReadRawSample(
        byte adcSelector,
        AsicRfTransaction transaction,
        AsicTimeGeneratorActionExecution action)
    {
        AdcSelector = adcSelector;
        Transaction = transaction;
        return 0x1234;
    }
}
