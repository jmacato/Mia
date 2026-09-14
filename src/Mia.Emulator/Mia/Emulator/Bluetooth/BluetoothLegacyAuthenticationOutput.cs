namespace Mia.Emulator.Bluetooth;

internal readonly ref struct BluetoothLegacyAuthenticationOutput
{
    internal BluetoothLegacyAuthenticationOutput(
        Span<byte> response,
        Span<byte> authenticatedCipheringOffset)
    {
        Response = response;
        AuthenticatedCipheringOffset = authenticatedCipheringOffset;
    }

    internal Span<byte> Response { get; }

    internal Span<byte> AuthenticatedCipheringOffset { get; }
}
