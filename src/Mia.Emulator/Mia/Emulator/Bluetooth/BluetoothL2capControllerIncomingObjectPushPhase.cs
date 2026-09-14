// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

internal enum BluetoothL2capControllerIncomingObjectPushPhase
{
    Disabled,
    AwaitOutboundFinalPut,
    AwaitPnResponse,
    AwaitUa,
    AwaitMsc,
    AwaitConnectResponse,
    AwaitPutResponse,
    AwaitDisconnectResponse,
    AwaitDiscUa,
    Complete,
}
