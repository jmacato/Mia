// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewTextEventArgs(string text) : EventArgs
{
    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));
}
