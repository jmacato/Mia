// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Modem.Bluetooth;

internal readonly record struct ArmModemBluetoothPeripheralPendingDspInboundTransfer(
    byte Control,
    byte Kind,
    byte[] Payload);
