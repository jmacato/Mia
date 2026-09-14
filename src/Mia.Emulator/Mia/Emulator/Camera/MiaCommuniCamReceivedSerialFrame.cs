namespace Mia.Emulator.Camera;

internal readonly ref struct MiaCommuniCamReceivedSerialFrame
{
    internal MiaCommuniCamReceivedSerialFrame(
        byte address,
        byte control,
        ReadOnlySpan<byte> information)
    {
        Address = address;
        Control = control;
        Information = information;
    }

    internal byte Address { get; }

    internal byte Control { get; }

    internal ReadOnlySpan<byte> Information { get; }
}
