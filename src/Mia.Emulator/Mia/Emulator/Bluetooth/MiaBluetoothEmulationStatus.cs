// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

/// <summary>
/// Thread-safe snapshot of the deterministic Bluetooth peer exposed to the
/// native handset firmware. All counters describe controller emulation; no
/// host Bluetooth adapter is involved.
/// </summary>
internal readonly record struct MiaBluetoothEmulationStatus(
    bool Enabled,
    string PeerName,
    string PeerAddress,
    string Pin,
    bool IncomingObjectPushEnabled,
    long InquiryResponseCount,
    long RemoteNameResponseCount,
    long PairingCompletedCount,
    long PairingAuthenticationFailureCount,
    long ConnectionCompletedCount,
    long ConnectionAuthenticationFailureCount,
    long SdpRequestCount,
    long ObexPutRequestCount,
    long TransferredObjectByteCount,
    long IncomingObjectPushCompletedCount);
