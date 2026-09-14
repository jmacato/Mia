// SPDX-License-Identifier: MIT

using System.Net;
using Mia.Emulator.Bluetooth;
using Mia.Emulator.Modem.Infrared;
using Mia.Emulator.Runtime;
using Mia.Emulator.Status;

namespace Mia.Desktop.Presentation;

/// <summary>
/// Maps engine-owned state to the host-neutral DTOs consumed by Mia.App.
/// </summary>
internal sealed class EmulatorSessionAdapter : IMainViewSession
{
    readonly EmulatorSession _session;
    readonly DesktopAutomationServer _automation;
    Task _automationStartup = Task.CompletedTask;
    int _automationStarted;
    int _disposeStarted;

    public EmulatorSessionAdapter(DesktopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _session = new EmulatorSession(options);
        _automation = new DesktopAutomationServer(options.AutomationPort, _session);

        _session.StatusChanged += value =>
            StatusChanged?.Invoke(this, new MainViewTextEventArgs(value));
        _session.OutputReceived += value =>
            OutputReceived?.Invoke(this, new MainViewTextEventArgs(value));
        _session.GsmMessageEmitted += value =>
            GsmMessageEmitted?.Invoke(this, new MainViewTextEventArgs(value));
        _session.FrameReady += value =>
            FrameReady?.Invoke(this, new MainViewFrameEventArgs(value));
        _session.AudioReady += value =>
            AudioReady?.Invoke(this, new MainViewAudioEventArgs(value));
        _session.StatusLedsChanged += MapStatusLeds;
        _session.PhoneLedsChanged += MapPhoneLeds;
        _session.BluetoothStatusChanged += MapBluetoothStatus;
        _session.InfraredStatusChanged += MapInfraredStatus;
        _session.CommuniCamConnectionChanged += value =>
            CommuniCamConnectionChanged?.Invoke(
                this,
                new MainViewCommuniCamEventArgs(value));
    }

    public event EventHandler<MainViewTextEventArgs>? StatusChanged;
    public event EventHandler<MainViewTextEventArgs>? OutputReceived;
    public event EventHandler<MainViewTextEventArgs>? GsmMessageEmitted;
    public event EventHandler<MainViewFrameEventArgs>? FrameReady;
    public event EventHandler<MainViewAudioEventArgs>? AudioReady;
    public event EventHandler<MainViewStatusLedEventArgs>? StatusLedsChanged;
    public event EventHandler<MainViewPhoneLedEventArgs>? PhoneLedsChanged;
    public event EventHandler<MainViewBluetoothStatusEventArgs>?
        BluetoothStatusChanged;
    public event EventHandler<MainViewInfraredStatusEventArgs>?
        InfraredStatusChanged;
    public event EventHandler<MainViewCommuniCamEventArgs>?
        CommuniCamConnectionChanged;

    public bool IsRunning => _session.IsRunning;
    public bool IsCommuniCamConnected => _session.IsCommuniCamConnected;
    public bool IsCommuniCamOperationPending =>
        _session.IsCommuniCamOperationPending;
    public bool IsHostCameraAvailable => _session.IsHostCameraAvailable;
    public string? HostCameraUnavailableReason =>
        _session.HostCameraUnavailableReason;

    public void BeginAutomation()
    {
        if (Interlocked.Exchange(ref _automationStarted, 1) == 0)
        {
            _automationStartup = StartAutomationAsync();
        }
    }

    public async Task StartAsync()
    {
        await _automationStartup.ConfigureAwait(true);
        await _session.StartAsync().ConfigureAwait(true);
    }

    public Task StopAsync() => _session.StopAsync();
    public Task StopForRestartAsync() => _session.StopForRestartAsync();
    public Task ConnectCommuniCamAsync() => _session.ConnectCommuniCamAsync();
    public Task DisconnectCommuniCamAsync() =>
        _session.DisconnectCommuniCamAsync();

    public void SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed) =>
        _session.SetKey(scanMask, rowMask, secondaryScanMask, pressed);

    public void SetPowerKey(bool pressed) => _session.SetPowerKey(pressed);

    public bool TryQueueIncomingSms(
        string originator,
        string text,
        out string result) =>
        _session.TryQueueIncomingSms(originator, text, out result);

    public bool TryQueueIncomingCall(
        string originator,
        bool autoAnswer,
        out string result) =>
        _session.TryQueueIncomingCall(originator, autoAnswer, out result);

    public bool TrySetCarrierRawSample(int rawSample, out string result) =>
        _session.TrySetCarrierRawSample(rawSample, out result);

    public bool TryConfigureBluetoothPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled,
        out string result) =>
        _session.TryConfigureBluetoothPeer(
            enabled,
            pin,
            incomingObjectPushEnabled,
            out result);

    public bool TryStageBluetoothObject(
        string name,
        string mediaType,
        ReadOnlyMemory<byte> data,
        out string result) =>
        _session.TryStageBluetoothObject(name, mediaType, data, out result);

    public bool TryGetBluetoothReceivedObject(
        out MainViewTransferObject value,
        out string result)
    {
        bool available = _session.TryGetBluetoothReceivedObject(
            out MiaTransferObject transfer,
            out result);
        value = available ? MapTransfer(transfer) : default;
        return available;
    }

    public bool TryStageInfraredObject(
        MainViewTransferObject value,
        out string result) =>
        _session.TryStageInfraredObject(
            new MiaTransferObject(value.Name, value.MediaType, value.Data),
            out result);

    public bool TryGetInfraredReceivedObject(
        out MainViewTransferObject value,
        out string result)
    {
        bool available = _session.TryGetInfraredReceivedObject(
            out MiaTransferObject transfer,
            out result);
        value = available ? MapTransfer(transfer) : default;
        return available;
    }

    async Task StartAutomationAsync()
    {
        try
        {
            await _automation.StartAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            OutputReceived?.Invoke(
                this,
                new MainViewTextEventArgs(
                    $"Automation server unavailable: {error.Message}"));
        }
    }

    void MapStatusLeds(MiaStatusLedState value) =>
        StatusLedsChanged?.Invoke(this, new(
            value.BluetoothBlue,
            value.NetworkGreen));

    void MapPhoneLeds(MiaPhoneLedState value) =>
        PhoneLedsChanged?.Invoke(this, new(
            value.LeftRed,
            value.LeftGreen,
            value.RightBlue,
            value.BacklightsOn));

    void MapBluetoothStatus(MiaBluetoothEmulationStatus value) =>
        BluetoothStatusChanged?.Invoke(this, new MainViewBluetoothStatusEventArgs
        {
            Enabled = value.Enabled,
            PeerName = value.PeerName,
            PeerAddress = value.PeerAddress,
            InquiryResponseCount = ClampCount(value.InquiryResponseCount),
            RemoteNameResponseCount = ClampCount(value.RemoteNameResponseCount),
            PairingCompletedCount = ClampCount(value.PairingCompletedCount),
            ConnectionCompletedCount = ClampCount(value.ConnectionCompletedCount),
            SdpRequestCount = ClampCount(value.SdpRequestCount),
            ObexPutRequestCount = ClampCount(value.ObexPutRequestCount),
            TransferredObjectByteCount = ClampCount(value.TransferredObjectByteCount),
            IncomingObjectPushCompletedCount =
                ClampCount(value.IncomingObjectPushCompletedCount),
            PairingAuthenticationFailureCount =
                ClampCount(value.PairingAuthenticationFailureCount),
            ConnectionAuthenticationFailureCount =
                ClampCount(value.ConnectionAuthenticationFailureCount),
        });

    void MapInfraredStatus(MiaInfraredTransferStatus value) =>
        InfraredStatusChanged?.Invoke(this, new(
            value.Message,
            value.ReceivedObject.HasValue));

    static int ClampCount(long value) =>
        (int)Math.Clamp(value, 0, int.MaxValue);

    static MainViewTransferObject MapTransfer(MiaTransferObject value) =>
        new(value.Name, value.MediaType, value.Data);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await _automationStartup.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _automation.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
