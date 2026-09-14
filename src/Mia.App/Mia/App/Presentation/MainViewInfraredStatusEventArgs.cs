// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewInfraredStatusEventArgs(
    string message,
    bool hasReceivedObject) : EventArgs
{
    public string Message { get; } = message ??
        throw new ArgumentNullException(nameof(message));
    public bool HasReceivedObject { get; } = hasReceivedObject;
}
