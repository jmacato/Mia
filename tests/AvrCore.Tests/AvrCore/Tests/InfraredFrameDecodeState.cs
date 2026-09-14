// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal sealed class InfraredFrameDecodeState
{
    public List<byte> Decoded { get; } = [];

    public bool Receiving { get; set; }

    public bool Escaped { get; set; }
}
