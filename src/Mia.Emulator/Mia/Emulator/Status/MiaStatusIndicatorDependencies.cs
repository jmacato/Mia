// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Status;

internal sealed record MiaStatusIndicatorDependencies(
    ArmModemBus ModemBus,
    ArmModemBluetoothPeripheral BluetoothPeripheral,
    MiaPowerPortController PowerPorts,
    MiaSystemClock Clock,
    MiaWorker ClockWorker);
