// SPDX-License-Identifier: MIT

namespace AvrCore.Execution;

public sealed class CpuGlobalInterruptEnableChangedEventArgs(bool enabled) : EventArgs
{
    public bool Enabled { get; } = enabled;
}
