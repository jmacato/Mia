// SPDX-License-Identifier: MIT

using Mia.App.Presentation;

namespace Mia.App.Tests;

internal sealed class RecordingMainViewSession : IMainViewSession
{
    bool _disposed;

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

    public bool IsRunning { get; set; }
    public bool IsCommuniCamConnected { get; set; }
    public bool IsCommuniCamOperationPending { get; init; }
    public bool IsHostCameraAvailable { get; init; } = true;
    public string? HostCameraUnavailableReason { get; init; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int StopForRestartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public (string Number, bool AutoAnswer)? LastIncomingCall { get; private set; }
    public MainViewTransferObject? StagedBluetoothObject { get; private set; }
    public MainViewTransferObject? StagedInfraredObject { get; private set; }
    public RecordedMainViewKey? LastKey { get; private set; }
    public bool? LastPowerKeyPressed { get; private set; }

    public Task StartAsync()
    {
        StartCount++;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        IsRunning = false;
        if (IsCommuniCamConnected)
        {
            IsCommuniCamConnected = false;
            CommuniCamConnectionChanged?.Invoke(
                this,
                new MainViewCommuniCamEventArgs(false));
        }
        StatusChanged?.Invoke(this, new MainViewTextEventArgs("Stopped"));
        return Task.CompletedTask;
    }

    public Task StopForRestartAsync()
    {
        StopForRestartCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public void SetKey(
        byte scanMask,
        byte rowMask,
        byte? secondaryScanMask,
        bool pressed) => LastKey = new RecordedMainViewKey(
            scanMask,
            rowMask,
            secondaryScanMask,
            pressed);

    public void SetPowerKey(bool pressed) => LastPowerKeyPressed = pressed;

    public bool TryQueueIncomingCall(
        string originator,
        bool autoAnswer,
        out string result)
    {
        LastIncomingCall = (originator, autoAnswer);
        result = "queued call";
        return true;
    }

    public bool TryQueueIncomingSms(string originator, string text, out string result)
    {
        result = $"queued SMS from {originator}: {text}";
        return true;
    }

    public bool TrySetCarrierRawSample(int rawSample, out string result)
    {
        result = $"RSSI {rawSample}";
        return true;
    }

    public bool TryConfigureBluetoothPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled,
        out string result)
    {
        result = $"peer {enabled} {pin} {incomingObjectPushEnabled}";
        return true;
    }

    public bool TryStageBluetoothObject(
        string name,
        string mediaType,
        ReadOnlyMemory<byte> data,
        out string result)
    {
        StagedBluetoothObject = new MainViewTransferObject(name, mediaType, data);
        result = "Bluetooth staged";
        return true;
    }

    public bool TryGetBluetoothReceivedObject(
        out MainViewTransferObject value,
        out string result)
    {
        value = new MainViewTransferObject(
            "received.vcf", "TEXT/X-VCARD", new byte[] { 4, 5 });
        result = "Bluetooth received";
        return true;
    }

    public bool TryStageInfraredObject(
        MainViewTransferObject value,
        out string result)
    {
        StagedInfraredObject = value;
        result = "Infrared staged";
        return true;
    }

    public bool TryGetInfraredReceivedObject(
        out MainViewTransferObject value,
        out string result)
    {
        value = new MainViewTransferObject(
            "received.vcf", "TEXT/X-VCARD", new byte[] { 6, 7 });
        result = "Infrared received";
        return true;
    }

    public Task ConnectCommuniCamAsync()
    {
        IsCommuniCamConnected = true;
        CommuniCamConnectionChanged?.Invoke(
            this,
            new MainViewCommuniCamEventArgs(true));
        return Task.CompletedTask;
    }

    public Task DisconnectCommuniCamAsync()
    {
        IsCommuniCamConnected = false;
        CommuniCamConnectionChanged?.Invoke(
            this,
            new MainViewCommuniCamEventArgs(false));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        DisposeCount++;
        OutputReceived?.Invoke(this, new MainViewTextEventArgs("disposed"));
        StatusChanged?.Invoke(this, new MainViewTextEventArgs("Stopped"));
        return ValueTask.CompletedTask;
    }

    public void PublishOutput(string value) => OutputReceived?.Invoke(
        this, new MainViewTextEventArgs(value));
    public void PublishStatus(string value) => StatusChanged?.Invoke(
        this, new MainViewTextEventArgs(value));
    public void PublishGsmMessage(string value) => GsmMessageEmitted?.Invoke(
        this, new MainViewTextEventArgs(value));
    public void PublishFrame(ReadOnlyMemory<byte> value) => FrameReady?.Invoke(
        this, new MainViewFrameEventArgs(value));
    public void PublishAudio(ReadOnlyMemory<short> value) => AudioReady?.Invoke(
        this, new MainViewAudioEventArgs(value));
    public void PublishStatusLeds(MainViewStatusLedEventArgs value) =>
        StatusLedsChanged?.Invoke(this, value);
    public void PublishPhoneLeds(MainViewPhoneLedEventArgs value) =>
        PhoneLedsChanged?.Invoke(this, value);
    public void PublishBluetoothStatus(MainViewBluetoothStatusEventArgs value) =>
        BluetoothStatusChanged?.Invoke(this, value);
    public void PublishInfraredStatus(MainViewInfraredStatusEventArgs value) =>
        InfraredStatusChanged?.Invoke(this, value);
}
