// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal sealed class AsicFchDetectorTestsCapturingSource(byte[] result) : IAsicFchSource
{
    public AsicFchRequest? Request { get; private set; }

    public AsicFchResult Detect(AsicFchRequest request)
    {
        Request = request;
        return new(true, result);
    }
}
