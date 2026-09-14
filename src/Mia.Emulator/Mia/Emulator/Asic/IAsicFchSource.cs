// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal interface IAsicFchSource
{
    AsicFchResult Detect(AsicFchRequest request);
}
