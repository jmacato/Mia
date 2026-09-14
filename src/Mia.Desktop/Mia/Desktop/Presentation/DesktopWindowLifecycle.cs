// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Mia.Desktop.Presentation;

/// <summary>
/// Keeps the desktop process alive until emulator persistence and host-camera
/// resources have finished shutting down.
/// </summary>
internal sealed class DesktopWindowLifecycle(
    MainViewModel viewModel,
    IMainViewHost host)
{
    readonly MainViewModel _viewModel = viewModel ??
        throw new ArgumentNullException(nameof(viewModel));
    readonly IMainViewHost _host = host ??
        throw new ArgumentNullException(nameof(host));
    Window? _window;
    bool _closePending;
    bool _closeAllowed;

    public void Attach() => Dispatcher.UIThread.Post(AttachToMainWindow);

    void AttachToMainWindow()
    {
        _window = (Application.Current?.ApplicationLifetime as
            IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (_window is null)
        {
            _host.ReportError(
                "Desktop lifecycle",
                new InvalidOperationException(
                    "The shared main window was not created."));
            return;
        }

        _window.Closing += HandleClosing;
    }

    async void HandleClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_closeAllowed)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        _viewModel.BeginClose();
        try
        {
            await _viewModel.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or
            InvalidOperationException or ObjectDisposedException)
        {
            _host.ReportError("Desktop shutdown", exception);
        }
        finally
        {
            _closeAllowed = true;
            if (_window is not null)
            {
                _window.Closing -= HandleClosing;
                _window.Close();
            }
        }
    }
}
