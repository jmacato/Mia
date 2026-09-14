// SPDX-License-Identifier: MIT

using Avalonia;
using Mia.App;
using Mia.Desktop.Presentation;

namespace Mia.Desktop.Hosting;

internal static class Program
{
    static EmulatorSessionAdapter? _session;

    [STAThread]
    public static void Main(string[] args)
    {
        DesktopOptions options = DesktopOptions.Parse(args);
        var host = new DesktopMainViewHost();
        BuildDesktopApplication(() => CreateMainViewModel(options, host))
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildDesktopApplication(
        Func<MainViewModel> viewModelFactory) =>
        AppBuilder.Configure(() => new MiaApplication(viewModelFactory))
            .UsePlatformDetect()
            .WithInterFont();

    static MainViewModel CreateMainViewModel(
        DesktopOptions options,
        IMainViewHost host)
    {
        _session = new EmulatorSessionAdapter(options);
        EmulatorSessionAdapter session = _session;
        var viewModel = new MainViewModel(
            session,
            host,
            new MainViewOptions(
                $"Parallel AVR + ARM modem · {Path.GetFileName(options.ModemPath)} · " +
                $"GDFS overlay {Path.GetFileName(options.GdfsOverlayPath)}",
                MainViewCapabilities.Diagnostics |
                    MainViewCapabilities.LiveAudio |
                    MainViewCapabilities.CommuniCam,
                options.AutoStart,
                options.PaceToRealTime));
        session.BeginAutomation();
        new DesktopWindowLifecycle(viewModel, host).Attach();
        Console.WriteLine(
            $"Desktop: shared {typeof(Mia.App.Views.MainView).Name} composed.");
        return viewModel;
    }
}
