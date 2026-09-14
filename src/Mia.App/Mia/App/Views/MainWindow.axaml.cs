// SPDX-License-Identifier: MIT

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Mia.App.Presentation;

namespace Mia.App.Views;

public sealed partial class MainWindow : Window
{
    readonly MainView _contentView;
    bool _closePending;
    bool _closeAllowed;

    public MainWindow()
    {
        InitializeComponent();
        _contentView = this.FindControl<MainView>("ContentView")!;
        Closing += HandleClosing;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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
        MainViewModel? viewModel = DataContext as MainViewModel;
        viewModel?.BeginClose();
        try
        {
            await _contentView.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            viewModel?.ReportHostFailure("Window shutdown", error);
        }
        finally
        {
            _closeAllowed = true;
            Close();
        }
    }
}
