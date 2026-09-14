// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public static class MainViewExceptions
{
    public static bool IsRecoverable(Exception exception) => exception is
        IOException or
        InvalidDataException or
        UnauthorizedAccessException or
        InvalidOperationException or
        ObjectDisposedException or
        ArgumentException or
        PlatformNotSupportedException or
        OperationCanceledException or
        DllNotFoundException or
        EntryPointNotFoundException or
        BadImageFormatException or
        NotSupportedException;
}
