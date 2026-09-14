// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class AsicEqualizerTestsFixedEqualizerSource(AsicEqualizerResult result) :
    IAsicEqualizerSource
{
    public AsicEqualizerResult Equalize(AsicEqualizerRequest request) =>
        result;
}
