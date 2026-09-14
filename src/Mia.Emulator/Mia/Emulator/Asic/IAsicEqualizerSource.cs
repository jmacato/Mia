// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal interface IAsicEqualizerSource
{
    AsicEqualizerResult Equalize(AsicEqualizerRequest request);
}
