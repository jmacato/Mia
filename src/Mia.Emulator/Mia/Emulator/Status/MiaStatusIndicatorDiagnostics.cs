// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Status;

/// <summary>
/// Evidence retained at the native compatibility boundaries that drive the
/// top indicators. The Bluetooth operation value is intentionally opaque:
/// firmware traces prove that it is not a complete off/automatic/on enum.
/// </summary>
internal readonly record struct MiaStatusIndicatorDiagnostics(
    MiaStatusLedState State,
    bool NetworkRegistered,
    byte NativeMphState,
    long NetworkStateChangeCount,
    long BluetoothControllerRecordCount,
    long BluetoothOperationRequestCount,
    long BluetoothOperationCompletionCount,
    long BluetoothDspPacketCount,
    long BluetoothDspTransferCount,
    byte LastBluetoothOperationValue,
    bool ChargingActive,
    bool BluetoothSteadyDueToCharging,
    MiaBluetoothControllerStates BluetoothControllerStates,
    long BluetoothGlobalStateCommandCount,
    long BluetoothLinkStateCommandCount);
