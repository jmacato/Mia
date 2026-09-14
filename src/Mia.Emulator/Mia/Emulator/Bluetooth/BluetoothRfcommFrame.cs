namespace Mia.Emulator.Bluetooth;

internal readonly ref struct BluetoothRfcommFrame
{
    internal BluetoothRfcommFrame(
        int length,
        byte dlci,
        byte control,
        ReadOnlySpan<byte> information)
    {
        Length = length;
        Dlci = dlci;
        Control = control;
        Information = information;
    }

    internal int Length { get; }

    internal byte Dlci { get; }

    internal byte Control { get; }

    internal byte FrameType => (byte)(Control & 0xef);

    internal bool PollFinal => (Control & 0x10) != 0;

    internal ReadOnlySpan<byte> Information { get; }
}
