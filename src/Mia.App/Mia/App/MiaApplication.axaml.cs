// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mia.App.Presentation;
using Mia.App.Views;

namespace Mia.App;

public sealed partial class MiaApplication : Application
{
    readonly Func<MainViewModel> _viewModelFactory;

    public MiaApplication() : this(RequireConfiguredFactory)
    {
    }

    public MiaApplication(Func<MainViewModel> viewModelFactory)
    {
        _viewModelFactory = viewModelFactory ??
            throw new ArgumentNullException(nameof(viewModelFactory));
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _viewModelFactory(),
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = _viewModelFactory(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    static MainViewModel RequireConfiguredFactory() =>
        throw new InvalidOperationException(
            "Configure MiaApplication with a MainViewModel factory.");
}
