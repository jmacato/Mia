// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mia.App.Presentation;

public sealed class BluetoothViewModel : ObservableObject, IDisposable
{
    const int MaximumTransferBytes = 1_048_576;
    readonly IMainViewSession _session;
    readonly IMainViewHost _host;
    readonly MainViewAsyncGate _gate;
    string _pin = "0000";
    string _result =
        "Mia Peer · 11:22:33:44:55:66 · waiting for handset discovery";
    bool _emulatePeer = true;
    bool _returnObject = true;
    int _disposeStarted;

    public BluetoothViewModel(
        IMainViewSession session,
        IMainViewHost host,
        MainViewAsyncGate gate)
    {
        _session = session;
        _host = host;
        _gate = gate;
        ApplySettingsCommand = new RelayCommand(
            ApplySettings,
            () => _gate.IsAvailable);
        StageFileCommand = CreateCommand(StageFileAsync);
        SaveFileCommand = CreateCommand(SaveFileAsync);
        _session.BluetoothStatusChanged += OnStatusChanged;
        _gate.AvailabilityChanged += NotifyCommandState;
    }

    public bool EmulatePeer
    {
        get => _emulatePeer;
        set => SetProperty(ref _emulatePeer, value);
    }

    public string Pin
    {
        get => _pin;
        set => SetProperty(ref _pin, value ?? string.Empty);
    }

    public bool ReturnObject
    {
        get => _returnObject;
        set => SetProperty(ref _returnObject, value);
    }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public IRelayCommand ApplySettingsCommand { get; }
    public IAsyncRelayCommand StageFileCommand { get; }
    public IAsyncRelayCommand SaveFileCommand { get; }

    public void ApplySettings()
    {
        try
        {
            _session.TryConfigureBluetoothPeer(
                EmulatePeer, Pin, ReturnObject, out string result);
            Result = result;
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            ReportFailure("Apply Bluetooth settings", error);
        }
    }

    public void MarkStopped() =>
        Result = "Emulated peer stopped · settings retained for the next boot";

    AsyncRelayCommand CreateCommand(Func<Task> operation) =>
        new AsyncRelayCommand(
            () => RunAsync(operation),
            () => _gate.IsAvailable,
            AsyncRelayCommandOptions.None);

    async Task RunAsync(Func<Task> operation)
    {
        try
        {
            if (!await _gate.TryRunAsync(operation).ConfigureAwait(true))
            {
                Result = "Another emulator operation is in progress.";
            }
        }
        catch (Exception error) when (MainViewExceptions.IsRecoverable(error))
        {
            ReportFailure("Bluetooth file transfer", error);
        }
    }

    async Task StageFileAsync()
    {
        MainViewHostFile? selected = await _host.PickFileAsync(
            "Stage a file for Bluetooth Object Push").ConfigureAwait(true);
        if (selected is not { } file)
        {
            return;
        }

        if (file.Data.Length > MaximumTransferBytes)
        {
            Result = "Bluetooth staging is limited to 1 MiB per object.";
            return;
        }

        ReturnObject = true;
        _session.TryStageBluetoothObject(
            file.Name,
            MainViewMediaTypes.Guess(file.Name),
            file.Data,
            out string result);
        Result = result;
    }

    async Task SaveFileAsync()
    {
        if (!_session.TryGetBluetoothReceivedObject(
                out MainViewTransferObject transferObject,
                out string result))
        {
            Result = result;
            return;
        }

        bool saved = await _host.SaveFileAsync(
            transferObject.Name,
            transferObject.MediaType,
            transferObject.Data).ConfigureAwait(true);
        Result = saved ? $"{result} Saved without byte conversion." : result;
    }

    void OnStatusChanged(object? sender, MainViewBluetoothStatusEventArgs eventArgs) =>
        _host.Post(() => Result = FormatStatus(eventArgs));

    static string FormatStatus(MainViewBluetoothStatusEventArgs status)
    {
        if (!status.Enabled)
        {
            return $"{status.PeerName} · emulated peer disabled";
        }

        return $"{status.PeerName} · {status.PeerAddress} · " +
            ResolvePhase(status);
    }

    static string ResolvePhase(MainViewBluetoothStatusEventArgs status) => status switch
    {
        _ when status.PairingAuthenticationFailureCount > 0 ||
            status.ConnectionAuthenticationFailureCount > 0 =>
            "authentication rejected",
        { IncomingObjectPushCompletedCount: > 0 } => "peer object delivered",
        { TransferredObjectByteCount: > 0 } =>
            $"received {status.TransferredObjectByteCount:n0} B",
        { ObexPutRequestCount: > 0 } => "OBEX object transfer active",
        { SdpRequestCount: > 0 } => "SDP service lookup active",
        { ConnectionCompletedCount: > 0 } => "bonded link connected",
        { PairingCompletedCount: > 0 } => "paired by handset firmware",
        { RemoteNameResponseCount: > 0 } => "name returned to handset",
        { InquiryResponseCount: > 0 } => "discovered by handset",
        _ => "waiting for handset discovery",
    };

    void NotifyCommandState(object? sender, EventArgs eventArgs)
    {
        ApplySettingsCommand.NotifyCanExecuteChanged();
        StageFileCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
    }

    void ReportFailure(string operation, Exception error)
    {
        Result = $"{operation} failed · {error.Message}";
        _host.ReportError(operation, error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _session.BluetoothStatusChanged -= OnStatusChanged;
        _gate.AvailabilityChanged -= NotifyCommandState;
    }
}
