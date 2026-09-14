// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mia.App.Presentation;

public sealed class CommuniCamViewModel : ObservableObject, IDisposable
{
    readonly IMainViewSession _session;
    readonly IMainViewHost _host;
    readonly MainViewAsyncGate _gate;
    bool _isConnected;
    bool _isEnabled;
    string _toolTip = "Host camera unavailable";
    int _disposeStarted;

    public CommuniCamViewModel(
        IMainViewSession session,
        IMainViewHost host,
        MainViewAsyncGate gate,
        MainViewCapabilities capabilities)
    {
        _session = session;
        _host = host;
        _gate = gate;
        IsVisible = capabilities.HasFlag(MainViewCapabilities.CommuniCam);
        ToggleCommand = new AsyncRelayCommand(
            ToggleAsync,
            () => IsEnabled,
            AsyncRelayCommandOptions.None);
        _session.CommuniCamConnectionChanged += OnConnectionChanged;
        _gate.AvailabilityChanged += OnAvailabilityChanged;
        Update();
    }

    public bool IsVisible { get; }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                ToggleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ToolTip
    {
        get => _toolTip;
        private set => SetProperty(ref _toolTip, value);
    }

    public IAsyncRelayCommand ToggleCommand { get; }

    public void Update()
    {
        IsConnected = _session.IsCommuniCamConnected;
        IsEnabled =
            IsVisible &&
            _gate.IsAvailable &&
            _session.IsRunning &&
            _session.IsHostCameraAvailable &&
            !_session.IsCommuniCamOperationPending;
        ToolTip = ResolveToolTip();
    }

    async Task ToggleAsync()
    {
        try
        {
            if (!await _gate.TryRunAsync(ToggleCoreAsync).ConfigureAwait(true))
            {
                return;
            }
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            _host.ReportError("CommuniCam", error);
        }
        finally
        {
            Update();
        }
    }

    Task ToggleCoreAsync() => _session.IsCommuniCamConnected
        ? _session.DisconnectCommuniCamAsync()
        : _session.ConnectCommuniCamAsync();

    string ResolveToolTip()
    {
        if (!IsVisible || !_session.IsHostCameraAvailable)
        {
            return _session.HostCameraUnavailableReason ?? "Host camera unavailable";
        }

        if (!_session.IsRunning)
        {
            return "Start the emulator to connect CommuniCam";
        }

        return _session.IsCommuniCamConnected
            ? "Disconnect CommuniCam"
            : "Connect CommuniCam to host camera";
    }

    void OnConnectionChanged(
        object? sender,
        MainViewCommuniCamEventArgs eventArgs) => _host.Post(Update);

    void OnAvailabilityChanged(object? sender, EventArgs eventArgs) => Update();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _session.CommuniCamConnectionChanged -= OnConnectionChanged;
        _gate.AvailabilityChanged -= OnAvailabilityChanged;
    }
}
