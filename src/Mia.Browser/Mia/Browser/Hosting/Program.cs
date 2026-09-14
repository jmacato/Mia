// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Browser;

namespace Mia.Browser.Hosting;

internal static class Program
{
    const string AssetBasePrefix = "--asset-base=";
    static BrowserEmulatorSession? _session;

    static async Task<BrowserEmulatorSession> LoadSessionAsync(string[] args)
    {
        var assetBase = ReadAssetBase(args);
        await JSHost.ImportAsync(
            BrowserPageInterop.ModuleName,
            new Uri(assetBase, "browser-page.js").AbsoluteUri).ConfigureAwait(false);
        var firmwareTask = BrowserPageInterop.LoadImageAsync(new Uri(assetBase, "firmware/flat.bin").AbsoluteUri);
        var gdfsTask = BrowserPageInterop.LoadImageAsync(new Uri(assetBase, "firmware/gdfs.raw").AbsoluteUri);
        var modemTask = BrowserPageInterop.LoadImageAsync(new Uri(assetBase, "firmware/modem.bih").AbsoluteUri);
        await Task.WhenAll(firmwareTask, gdfsTask, modemTask).ConfigureAwait(false);
        var firmware = await firmwareTask.ConfigureAwait(false);
        var gdfs = await gdfsTask.ConfigureAwait(false);
        var modem = await modemTask.ConfigureAwait(false);
        var diagnostics = args.Contains("--diagnostics", StringComparer.Ordinal);
        var paceToRealTime = !args.Contains("--unthrottled", StringComparer.Ordinal);
        await JSHost.ImportAsync(
            BrowserPersistenceInterop.ModuleName,
            new Uri(assetBase, "persistence.js").AbsoluteUri).ConfigureAwait(false);
        await JSHost.ImportAsync(
            BrowserAudioInterop.ModuleName,
            new Uri(assetBase, "audio.js").AbsoluteUri).ConfigureAwait(false);
        await JSHost.ImportAsync(
            BrowserFileDownloadInterop.ModuleName,
            new Uri(assetBase, "file-download.js").AbsoluteUri).ConfigureAwait(false);
        var persistenceStore = new BrowserMiaPersistenceStore();
        string persistenceKey = MiaPersistence.CreateKey(firmware);
        return new BrowserEmulatorSession(
            MiaMachine.CreateCpu(MiaMachine.BuildProgram(firmware, gdfs)),
            MiaMachine.CreateModem(modem),
            diagnostics,
            paceToRealTime,
            persistenceStore,
            persistenceKey);
    }

    static async Task Main(string[] args)
    {
        var session = await LoadSessionAsync(args).ConfigureAwait(false);
        // Release the downloaded images before initializing
        // Avalonia, while retaining only the prepared emulated memory.
        await Task.Yield();
        GC.Collect();
        var diagnostics = args.Contains("--diagnostics", StringComparer.Ordinal);
        var paceToRealTime = !args.Contains("--unthrottled", StringComparer.Ordinal);
        var autoStart = !args.Contains("--no-auto-start", StringComparer.Ordinal);
        var host = new BrowserMainViewHost();
        var viewOptions = new MainViewOptions(
            $"WebAssembly · {RuntimeDescription}",
            (diagnostics ? MainViewCapabilities.Diagnostics : 0) |
                MainViewCapabilities.ReloadForRestart |
                (paceToRealTime ? MainViewCapabilities.LiveAudio : 0),
            autoStart,
            paceToRealTime);
        _session = session;

        await AppBuilder.Configure(() => new Mia.App.MiaApplication(
            () => new MainViewModel(session, host, viewOptions)))
            .WithInterFont()
            .StartBrowserAppAsync(
            "out",
            new BrowserPlatformOptions
            {
                // Keep the Avalonia dispatcher on the managed UI pthread.
                PreferManagedThreadDispatcher = true,
                RenderingMode =
                [
                    BrowserRenderingMode.WebGL2,
                    BrowserRenderingMode.WebGL1,
                    BrowserRenderingMode.Software2D,
                ],
            }).ConfigureAwait(false);
    }

#if BROWSER_THREADS
    const string RuntimeDescription =
        "Native AVR firmware + ARM modem · threaded WebAssembly runtime";
#else
    const string RuntimeDescription =
        "Native AVR firmware + ARM modem · cooperative browser runtime";
#endif

    static Uri ReadAssetBase(IEnumerable<string> args)
    {
        var argument = args.FirstOrDefault(
            value => value.StartsWith(AssetBasePrefix, StringComparison.Ordinal));
        if (argument is null ||
            !Uri.TryCreate(argument[AssetBasePrefix.Length..], UriKind.Absolute, out var assetBase))
        {
            throw new InvalidOperationException("Missing or invalid browser asset base URL.");
        }
        return assetBase;
    }
}
