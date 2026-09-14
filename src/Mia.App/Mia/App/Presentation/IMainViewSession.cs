// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public interface IMainViewSession : IAsyncDisposable
{
    event EventHandler<MainViewTextEventArgs>? StatusChanged;
    event EventHandler<MainViewTextEventArgs>? OutputReceived;
    event EventHandler<MainViewTextEventArgs>? GsmMessageEmitted;
    event EventHandler<MainViewFrameEventArgs>? FrameReady;
    event EventHandler<MainViewAudioEventArgs>? AudioReady;
    event EventHandler<MainViewStatusLedEventArgs>? StatusLedsChanged;
    event EventHandler<MainViewPhoneLedEventArgs>? PhoneLedsChanged;
    event EventHandler<MainViewBluetoothStatusEventArgs>? BluetoothStatusChanged;
    event EventHandler<MainViewInfraredStatusEventArgs>? InfraredStatusChanged;
    event EventHandler<MainViewCommuniCamEventArgs>? CommuniCamConnectionChanged;

    bool IsRunning { get; }
    bool IsCommuniCamConnected { get; }
    bool IsCommuniCamOperationPending { get; }
    bool IsHostCameraAvailable { get; }
    string? HostCameraUnavailableReason { get; }

    Task StartAsync();
    Task StopAsync();
    Task StopForRestartAsync();
    Task ConnectCommuniCamAsync();
    Task DisconnectCommuniCamAsync();

    void SetKey(byte scanMask, byte rowMask, byte? secondaryScanMask, bool pressed);
    void SetPowerKey(bool pressed);

    bool TryQueueIncomingSms(string originator, string text, out string result);
    bool TryQueueIncomingCall(string originator, bool autoAnswer, out string result);
    bool TrySetCarrierRawSample(int rawSample, out string result);
    bool TryConfigureBluetoothPeer(
        bool enabled,
        string pin,
        bool incomingObjectPushEnabled,
        out string result);
    bool TryStageBluetoothObject(
        string name,
        string mediaType,
        ReadOnlyMemory<byte> data,
        out string result);
    bool TryGetBluetoothReceivedObject(
        out MainViewTransferObject value,
        out string result);
    bool TryStageInfraredObject(MainViewTransferObject value, out string result);
    bool TryGetInfraredReceivedObject(
        out MainViewTransferObject value,
        out string result);
}
