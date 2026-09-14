// SPDX-License-Identifier: MIT

using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mia.App.Controls;
using Mia.App.Presentation;
using Mia.App.Views;
using Xunit;

namespace Mia.App.Tests;

public sealed class AxamlArchitectureTests
{
    static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void SharedApplicationLoadsItsCompiledThemeAndStyles()
    {
        var application = new MiaApplication();

        application.Initialize();

        Assert.Equal(2, application.Styles.Count);
    }

    [Fact]
    public void SharedWindowDeclaresItsViewModelAndContentSurface()
    {
        XElement root = LoadAxaml(
            "src/Mia.App/Mia/App/Views/MainWindow.axaml");

        Assert.Equal(
            "vm:MainViewModel",
            root.Attribute(XamlNamespace + "DataType")?.Value);
        AssertNamedSurfaces(root, "ContentView");
    }

    [Fact]
    public void SharedMainViewDeclaresItsViewModelAndNamedSurfaces()
    {
        XElement root = LoadAxaml(
            "src/Mia.App/Mia/App/Views/MainView.axaml");

        Assert.Equal(
            "vm:MainViewModel",
            root.Attribute(XamlNamespace + "DataType")?.Value);
        AssertNamedSurfaces(
            root,
            "PhoneFace",
            "ControlOverlayButton",
            "ControlOverlayIcon");
    }

    [Fact]
    public void ApplicationAndSharedControlResourcesRemainDeclarative()
    {
        XElement application = LoadAxaml(
            "src/Mia.App/Mia/App/MiaApplication.axaml");
        XElement phoneFace = LoadAxaml(
            "src/Mia.App/Mia/App/Controls/MiaPhoneFace.axaml");

        Assert.Contains(
            application.Descendants(),
            element => element.Name.LocalName == "FluentTheme");
        Assert.Contains(
            application.Descendants(),
            element => element.Name.LocalName == "StyleInclude");
        AssertNamedSurfaces(
            phoneFace,
            "PhoneCanvas",
            "DisplayLayer",
            "KeypadBacklight");
    }

    [Fact]
    public async Task DesktopLifetimeUsesFactoryAndSharedMainWindow()
    {
        var session = new RecordingMainViewSession();
        await using var sessionLease = session.ConfigureAwait(true);
        var viewModel = new MainViewModel(
            session,
            new RecordingMainViewHost(),
            new MainViewOptions(
                "test",
                MainViewCapabilities.None,
                AutoStart: true));
        using var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var application = new MiaApplication(() => viewModel)
        {
            ApplicationLifetime = lifetime,
        };

        AppBuilder.Configure(() => application)
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithLifetime(lifetime);

        MainWindow mainWindow = Assert.IsType<MainWindow>(lifetime.MainWindow);
        Assert.Same(viewModel, mainWindow.DataContext);
        mainWindow.Show();
        using (var loopCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250)))
        {
            Dispatcher.UIThread.MainLoop(loopCancellation.Token);
        }
        MiaPhoneFace phoneFace = Assert.Single(
            mainWindow.GetVisualDescendants().OfType<MiaPhoneFace>());
        phoneFace.InputEnabled = true;
        foreach (MiaPhoneKey key in Enum.GetValues<MiaPhoneKey>())
        {
            int sourceKey = (int)key;
            phoneFace.PressKeyboard(sourceKey, key);
            Assert.True(phoneFace.ReleaseKeyboard(sourceKey));
        }
        Assert.Equal(1, session.StartCount);
        await viewModel.DisposeAsync().ConfigureAwait(true);
    }

    static XElement LoadAxaml(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath)).Root ??
        throw new InvalidDataException($"AXAML has no root: {relativePath}");

    static void AssertNamedSurfaces(XElement root, params string[] expectedNames)
    {
        HashSet<string> actualNames = root
            .DescendantsAndSelf()
            .Select(element => element.Attribute(XamlNamespace + "Name")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (string expectedName in expectedNames)
        {
            Assert.Contains(expectedName, actualNames);
        }
    }
}
