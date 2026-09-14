// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewStatusLedEventArgs(
    bool bluetoothBlue,
    bool networkGreen) : EventArgs
{
    public bool BluetoothBlue { get; } = bluetoothBlue;
    public bool NetworkGreen { get; } = networkGreen;
}
