namespace Mia.Emulator.Bluetooth;

internal readonly record struct BluetoothRfcommFrameLayout(
    int InformationOffset,
    int InformationLength,
    int FcsHeaderLength);
