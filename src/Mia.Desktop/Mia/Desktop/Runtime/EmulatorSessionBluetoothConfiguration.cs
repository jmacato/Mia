// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Runtime;

internal sealed record EmulatorSessionBluetoothConfiguration(
    bool PeerEnabled,
    string PeerPin,
    bool IncomingObjectPushEnabled,
    MiaTransferObject? StagedObject);
