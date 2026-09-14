// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewBluetoothStatusEventArgs : EventArgs
{
    public bool Enabled { get; init; }
    public string PeerName { get; init; } = string.Empty;
    public string PeerAddress { get; init; } = string.Empty;
    public int InquiryResponseCount { get; init; }
    public int RemoteNameResponseCount { get; init; }
    public int PairingCompletedCount { get; init; }
    public int ConnectionCompletedCount { get; init; }
    public int SdpRequestCount { get; init; }
    public int ObexPutRequestCount { get; init; }
    public int TransferredObjectByteCount { get; init; }
    public int IncomingObjectPushCompletedCount { get; init; }
    public int PairingAuthenticationFailureCount { get; init; }
    public int ConnectionAuthenticationFailureCount { get; init; }
}
