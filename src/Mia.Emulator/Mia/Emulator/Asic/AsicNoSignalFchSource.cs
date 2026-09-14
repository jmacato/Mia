// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicNoSignalFchSource : IAsicFchSource
{
    public static AsicNoSignalFchSource Instance { get; } = new();

    AsicNoSignalFchSource()
    {
    }

    public AsicFchResult Detect(AsicFchRequest request) =>
        AsicFchResult.Failure;
}
