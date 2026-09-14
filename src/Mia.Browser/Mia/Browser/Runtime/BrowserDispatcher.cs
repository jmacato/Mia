// SPDX-License-Identifier: MIT

using Avalonia.Threading;

namespace Mia.Browser.Runtime;

internal static class BrowserDispatcher
{
    public static void Post(Action callback) =>
        Post(callback, DispatcherPriority.Normal);

    public static void Post(
        Action callback,
        DispatcherPriority priority)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Dispatcher.UIThread.Post(callback, priority);
    }
}
