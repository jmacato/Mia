// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewFrameEventArgs(ReadOnlyMemory<byte> frame) : EventArgs
{
    public ReadOnlyMemory<byte> Frame { get; } = frame;
}
