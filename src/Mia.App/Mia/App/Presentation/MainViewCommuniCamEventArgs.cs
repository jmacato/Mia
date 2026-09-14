// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewCommuniCamEventArgs(bool isConnected) : EventArgs
{
    public bool IsConnected { get; } = isConnected;
}
