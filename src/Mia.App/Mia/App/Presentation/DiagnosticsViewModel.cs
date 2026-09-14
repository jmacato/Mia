// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace Mia.App.Presentation;

public sealed class DiagnosticsViewModel : ObservableObject, IDisposable
{
    readonly IMainViewSession _session;
    readonly IMainViewHost _host;
    string _log = string.Empty;
    int _disposeStarted;

    public DiagnosticsViewModel(
        IMainViewSession session,
        IMainViewHost host,
        MainViewOptions options)
    {
        _session = session;
        _host = host;
        ArgumentNullException.ThrowIfNull(options);
        IsLogVisible = options.Capabilities.HasFlag(MainViewCapabilities.Diagnostics);
        _session.OutputReceived += OnOutputReceived;
    }

    public bool IsLogVisible { get; }

    public string Log
    {
        get => _log;
        private set => SetProperty(ref _log, value);
    }

    public void AppendError(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Append($"{operation} failed: {exception.Message}");
    }

    void OnOutputReceived(object? sender, MainViewTextEventArgs eventArgs) =>
        _host.Post(() => Append(eventArgs.Text));

    void Append(string line)
    {
        string existing = Log;
        if (existing.Length > 16_000)
        {
            existing = existing[^12_000..];
        }

        Log = string.IsNullOrEmpty(existing)
            ? line
            : $"{existing}{Environment.NewLine}{line}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            _session.OutputReceived -= OnOutputReceived;
        }
    }
}
