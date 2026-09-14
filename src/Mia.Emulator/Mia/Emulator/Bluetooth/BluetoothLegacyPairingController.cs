// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

/// <summary>
/// Remote-controller side of the legacy pairing exchange observed on the
/// modem's kind-7 DSP wire. This state belongs to the emulated Bluetooth
/// controller; the handset firmware remains the sole owner of UI and pairing
/// success state.
/// </summary>
internal sealed class BluetoothLegacyPairingController
{
    const int ControllerRecordLength =
        ArmModemBluetoothPeripheral.ControllerRecordLength;

    readonly byte[] _peerAddress;
    readonly byte[] _handsetAddress;
    readonly byte[] _pin;
    readonly byte[] _initializationKey = new byte[16];
    readonly byte[] _linkKey = new byte[16];
    readonly byte[] _expectedHandsetResponse = new byte[4];

    BluetoothLegacyPairingControllerWirePhase _phase;

    internal BluetoothLegacyPairingController(
        ReadOnlySpan<byte> peerAddress,
        ReadOnlySpan<byte> handsetAddress,
        ReadOnlySpan<byte> pin)
    {
        if (peerAddress.Length != 6)
        {
            throw new ArgumentException(
                "The emulated peer address must contain six bytes.",
                nameof(peerAddress));
        }
        if (handsetAddress.Length != 6)
        {
            throw new ArgumentException(
                "The emulated handset address must contain six bytes.",
                nameof(handsetAddress));
        }
        if (pin.IsEmpty || pin.Length > 10)
        {
            throw new ArgumentException(
                "The emulated peer PIN must contain 1..10 bytes.",
                nameof(pin));
        }

        _peerAddress = peerAddress.ToArray();
        _handsetAddress = handsetAddress.ToArray();
        _pin = pin.ToArray();
    }

    internal bool IsComplete => _phase == BluetoothLegacyPairingControllerWirePhase.Complete;

    internal bool AuthenticationFailed =>
        _phase == BluetoothLegacyPairingControllerWirePhase.AuthenticationFailed;

    internal ReadOnlySpan<byte> LinkKey => _linkKey;

    internal byte[] Begin()
    {
        _initializationKey.AsSpan().Clear();
        _linkKey.AsSpan().Clear();
        _expectedHandsetResponse.AsSpan().Clear();
        _phase = BluetoothLegacyPairingControllerWirePhase.WaitInitialFeatureResponse;

        // Selector 50 is the recovered incoming connection event. Its
        // 04-EA31 handle is echoed by the native 4E transaction.
        return CreateRecord(0x50, [0x04, 0xea, 0x31]);
    }

    internal IReadOnlyList<byte[]> ObserveOutbound(
        ReadOnlySpan<byte> payload) => _phase switch
        {
            BluetoothLegacyPairingControllerWirePhase.WaitInitialFeatureResponse =>
                ObserveInitialFeatureResponse(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitInitializationRandom =>
                ObserveInitializationRandom(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitInitializationAccepted =>
                ObserveInitializationAccepted(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitCombinationKey =>
                ObserveCombinationKey(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitAuthenticationChallenge =>
                ObserveAuthenticationChallenge(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitHandsetAuthentication =>
                ObserveHandsetAuthentication(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitHandsetAuthenticationAccepted =>
                ObserveHandsetAuthenticationAccepted(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitEncryptionMode =>
                ObserveEncryptionMode(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitEncryptionKeySize =>
                ObserveEncryptionKeySize(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitStartEncryption =>
                ObserveStartEncryption(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitFeatureRequest =>
                ObserveFeatureRequest(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitExtendedFeatureSequence =>
                ObserveExtendedFeatureSequence(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitCompletionRequest =>
                ObserveCompletionRequest(payload),
            BluetoothLegacyPairingControllerWirePhase.WaitFinalAccepted =>
                ObserveFinalAccepted(payload),
            _ => [],
        };

    IReadOnlyList<byte[]> ObserveInitialFeatureResponse(ReadOnlySpan<byte> payload)
    {
        if ((payload.Length != 9 || payload[0] != 0x4e) &&
            !payload.SequenceEqual(new byte[] { 0x66 }))
        {
            return [];
        }
        _phase = BluetoothLegacyPairingControllerWirePhase.WaitInitializationRandom;
        return [CreateRecord(0x06)];
    }

    IReadOnlyList<byte[]> ObserveInitializationRandom(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 17 || payload[0] != 0x10)
        {
            return [];
        }
        Span<byte> keyMaterial = stackalloc byte[
            _pin.Length + _peerAddress.Length];
        _pin.CopyTo(keyMaterial);
        _peerAddress.CopyTo(keyMaterial[_pin.Length..]);
        BluetoothLegacyCrypto.DeriveInitializationKey(
            payload[1..], keyMaterial, _initializationKey);
        keyMaterial.Clear();
        _phase = BluetoothLegacyPairingControllerWirePhase.WaitInitializationAccepted;
        return [CreateRecord(0x16)];
    }

    IReadOnlyList<byte[]> ObserveInitializationAccepted(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x09, 0x0b, 0x06],
            BluetoothLegacyPairingControllerWirePhase.WaitCombinationKey,
            [CreateRecord(0x06)]);

    IReadOnlyList<byte[]> ObserveCombinationKey(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 17 || payload[0] != 0x12)
        {
            return [];
        }

        Span<byte> combinationRandom = stackalloc byte[16];
        Xor(payload[1..], _initializationKey, combinationRandom);
        Span<byte> handsetPart = stackalloc byte[16];
        Span<byte> peerPart = stackalloc byte[16];
        BluetoothLegacyCrypto.DeriveCombinationKeyPart(
            combinationRandom, _handsetAddress, handsetPart);
        BluetoothLegacyCrypto.DeriveCombinationKeyPart(
            combinationRandom, _peerAddress, peerPart);
        Xor(handsetPart, peerPart, _linkKey);
        combinationRandom.Clear();
        handsetPart.Clear();
        peerPart.Clear();
        _phase = BluetoothLegacyPairingControllerWirePhase.WaitAuthenticationChallenge;
        return [CreateRecord(0x12, payload[1..])];
    }

    static void Xor(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right,
        Span<byte> result)
    {
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(left[index] ^ right[index]);
        }
    }

    IReadOnlyList<byte[]> ObserveAuthenticationChallenge(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 17 || payload[0] != 0x16)
        {
            return [];
        }

        Span<byte> response = stackalloc byte[4];
        Span<byte> cipheringOffset = stackalloc byte[12];
        BluetoothLegacyCrypto.Authenticate(
            _linkKey,
            payload[1..],
            _peerAddress,
            new BluetoothLegacyAuthenticationOutput(response, cipheringOffset));
        Span<byte> zeroChallenge = stackalloc byte[16];
        Span<byte> handsetCipheringOffset = stackalloc byte[12];
        BluetoothLegacyCrypto.Authenticate(
            _linkKey,
            zeroChallenge,
            _handsetAddress,
            new BluetoothLegacyAuthenticationOutput(
                _expectedHandsetResponse,
                handsetCipheringOffset));
        byte[] authenticationRecord = CreateRecord(0x18, response);
        response.Clear();
        cipheringOffset.Clear();
        zeroChallenge.Clear();
        handsetCipheringOffset.Clear();
        _phase = BluetoothLegacyPairingControllerWirePhase.WaitHandsetAuthentication;
        return [authenticationRecord, CreateRecord(0x16)];
    }

    IReadOnlyList<byte[]> ObserveHandsetAuthentication(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 5 || payload[0] != 0x18)
        {
            return [];
        }
        _phase = payload[1..].SequenceEqual(_expectedHandsetResponse)
            ? BluetoothLegacyPairingControllerWirePhase.WaitHandsetAuthenticationAccepted
            : BluetoothLegacyPairingControllerWirePhase.AuthenticationFailed;
        return [];
    }

    IReadOnlyList<byte[]> ObserveHandsetAuthenticationAccepted(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x1e, 0x01],
            BluetoothLegacyPairingControllerWirePhase.WaitEncryptionMode,
            [CreateRecord(0x1e)]);

    IReadOnlyList<byte[]> ObserveEncryptionMode(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x09, 0x0f, 0x23],
            BluetoothLegacyPairingControllerWirePhase.WaitEncryptionKeySize,
            [CreateRecord(0x06)]);

    IReadOnlyList<byte[]> ObserveEncryptionKeySize(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x20, 0x05],
            BluetoothLegacyPairingControllerWirePhase.WaitStartEncryption,
            [CreateRecord(0x20, [0x05])]);

    IReadOnlyList<byte[]> ObserveStartEncryption(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 17 || payload[0] != 0x22)
        {
            return [];
        }
        _phase = BluetoothLegacyPairingControllerWirePhase.WaitFeatureRequest;
        return [CreateRecord(0x06)];
    }

    IReadOnlyList<byte[]> ObserveFeatureRequest(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x62],
            BluetoothLegacyPairingControllerWirePhase.WaitExtendedFeatureSequence,
            [CreateRecord(0x62)]);

    IReadOnlyList<byte[]> ObserveExtendedFeatureSequence(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x0a],
            BluetoothLegacyPairingControllerWirePhase.WaitCompletionRequest,
            [CreateRecord(0x66), CreateRecord(0x6e, [0x80, 0x0c]), CreateRecord(0x0a)]);

    IReadOnlyList<byte[]> ObserveCompletionRequest(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x0d, 0x0d, 0xd0],
            BluetoothLegacyPairingControllerWirePhase.WaitFinalAccepted,
            [CreateRecord(0x0d, [0x0d, 0xd0])]);

    IReadOnlyList<byte[]> ObserveFinalAccepted(ReadOnlySpan<byte> payload) =>
        AdvanceWhenEqual(payload, [0x0e, 0x13],
            BluetoothLegacyPairingControllerWirePhase.Complete,
            []);

    IReadOnlyList<byte[]> AdvanceWhenEqual(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected,
        BluetoothLegacyPairingControllerWirePhase next,
        IReadOnlyList<byte[]> responses)
    {
        if (!actual.SequenceEqual(expected))
        {
            return [];
        }
        _phase = next;
        return responses;
    }

    static byte[] CreateRecord(
        byte selector,
        ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > ControllerRecordLength - 1)
        {
            throw new ArgumentException(
                "A controller record payload cannot exceed sixteen bytes.",
                nameof(payload));
        }

        var record = new byte[ControllerRecordLength];
        record[0] = selector;
        payload.CopyTo(record.AsSpan(1));
        return record;
    }
}
