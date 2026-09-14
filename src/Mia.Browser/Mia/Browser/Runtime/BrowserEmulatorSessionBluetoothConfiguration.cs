// SPDX-License-Identifier: MIT

namespace Mia.Browser.Runtime;

internal sealed record BrowserEmulatorSessionBluetoothConfiguration(
    bool PeerEnabled,
    string PeerPin,
    bool IncomingObjectPushEnabled,
    MiaTransferObject? StagedObject);
