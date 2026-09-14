// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

/// <summary>
/// Remote-controller side of a reconnect to an already bonded legacy peer.
/// It authenticates with the link key produced by the prior native pairing
/// exchange; it never asks the handset to pair again.
/// </summary>
internal sealed class BluetoothLegacyConnectionController
{
    const int ControllerRecordLength =
        ArmModemBluetoothPeripheral.ControllerRecordLength;

    readonly byte[] _peerAddress;
    readonly byte[] _handsetAddress;
    readonly byte[] _linkKey;
    readonly byte[] _expectedHandsetResponse = new byte[4];

    BluetoothLegacyConnectionControllerLinkPhase _linkPhase;
    BluetoothLegacyConnectionControllerSecurityPhase _securityPhase;

    internal BluetoothLegacyConnectionController(
        ReadOnlySpan<byte> peerAddress,
        ReadOnlySpan<byte> handsetAddress,
        ReadOnlySpan<byte> linkKey)
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
        if (linkKey.Length != 16)
        {
            throw new ArgumentException(
                "A legacy Bluetooth link key must contain sixteen bytes.",
                nameof(linkKey));
        }

        _peerAddress = peerAddress.ToArray();
        _handsetAddress = handsetAddress.ToArray();
        _linkKey = linkKey.ToArray();
    }

    internal bool IsComplete =>
        _linkPhase == BluetoothLegacyConnectionControllerLinkPhase.Complete &&
        _securityPhase == BluetoothLegacyConnectionControllerSecurityPhase.Complete;

    internal bool AuthenticationFailed =>
        _securityPhase == BluetoothLegacyConnectionControllerSecurityPhase.AuthenticationFailed;

    internal void ObserveDataChannelEstablished()
    {
        if (_securityPhase == BluetoothLegacyConnectionControllerSecurityPhase.Complete)
        {
            _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.Complete;
        }
    }

    internal IReadOnlyList<byte[]> Begin()
    {
        Span<byte> zeroChallenge = stackalloc byte[16];
        Span<byte> handsetCipheringOffset = stackalloc byte[12];
        BluetoothLegacyCrypto.Authenticate(
            _linkKey,
            zeroChallenge,
            _handsetAddress,
            new BluetoothLegacyAuthenticationOutput(
                _expectedHandsetResponse,
                handsetCipheringOffset));
        zeroChallenge.Clear();
        handsetCipheringOffset.Clear();
        _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.WaitInitialFeatureResponse;
        _securityPhase = BluetoothLegacyConnectionControllerSecurityPhase.None;

        // Selector 50 starts the recovered native connection path. Feature
        // acceptance and authentication are emitted only after the handset's
        // matching kind-7 response; they are not pre-timed state changes.
        return [CreateRecord(0x50, [0x04, 0xea, 0x31])];
    }

    internal IReadOnlyList<byte[]> ObserveOutbound(
        ReadOnlySpan<byte> payload)
    {
        var responses = new List<byte[]>();
        ObserveLinkNegotiation(payload, responses);
        ObserveSecurityNegotiation(payload, responses);
        return responses;
    }

    void ObserveLinkNegotiation(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        _ = _linkPhase switch
        {
            BluetoothLegacyConnectionControllerLinkPhase.WaitInitialFeatureResponse =>
                AcceptInitialFeatures(payload, responses),
            BluetoothLegacyConnectionControllerLinkPhase.WaitFeatureRequest =>
                AcceptFeatureRequest(payload, responses),
            BluetoothLegacyConnectionControllerLinkPhase.WaitExtendedFeatureSequence =>
                AcceptExtendedFeatures(payload, responses),
            BluetoothLegacyConnectionControllerLinkPhase.WaitCompletionRequest =>
                AcceptCompletionRequest(payload, responses),
            BluetoothLegacyConnectionControllerLinkPhase.WaitFinalAccepted =>
                AcceptFinalLinkState(payload),
            _ => false,
        };
    }

    bool AcceptInitialFeatures(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (!((payload.Length == 9 && payload[0] == 0x4e) ||
              payload.SequenceEqual(new byte[] { 0x66 })))
        {
            return false;
        }
        _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.WaitFeatureRequest;
        _securityPhase =
            BluetoothLegacyConnectionControllerSecurityPhase.ReadyToChallenge;
        responses.Add(CreateRecord(0x06));
        return true;
    }

    bool AcceptFeatureRequest(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (!payload.SequenceEqual(new byte[] { 0x62 }))
        {
            return false;
        }
        _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.WaitExtendedFeatureSequence;
        _securityPhase =
            BluetoothLegacyConnectionControllerSecurityPhase.WaitHandsetAuthentication;
        // Record ordering selects bonded authentication rather than pairing.
        responses.Add(CreateRecord(0x16));
        responses.Add(CreateRecord(0x62));
        return true;
    }

    bool AcceptExtendedFeatures(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (!payload.SequenceEqual(new byte[] { 0x0a }))
        {
            return false;
        }
        _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.WaitCompletionRequest;
        responses.Add(CreateRecord(0x66));
        responses.Add(CreateRecord(0x6e, [0x80, 0x0c]));
        responses.Add(CreateRecord(0x0a));
        return true;
    }

    bool AcceptCompletionRequest(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (!payload.SequenceEqual(new byte[] { 0x0d, 0x0d, 0xd0 }))
        {
            return false;
        }
        _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.WaitFinalAccepted;
        responses.Add(CreateRecord(0x0d, [0x0d, 0xd0]));
        return true;
    }

    bool AcceptFinalLinkState(ReadOnlySpan<byte> payload)
    {
        if (!payload.SequenceEqual(new byte[] { 0x0e, 0x13 }))
        {
            return false;
        }
        _linkPhase = BluetoothLegacyConnectionControllerLinkPhase.Complete;
        return true;
    }

    void ObserveSecurityNegotiation(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        _ = _securityPhase switch
        {
            BluetoothLegacyConnectionControllerSecurityPhase.WaitHandsetAuthentication =>
                AcceptHandsetAuthentication(payload, responses),
            BluetoothLegacyConnectionControllerSecurityPhase.WaitEncryptionMode =>
                AcceptEncryptionMode(payload, responses),
            BluetoothLegacyConnectionControllerSecurityPhase.WaitEncryptionKeySize =>
                AcceptEncryptionKeySize(payload, responses),
            BluetoothLegacyConnectionControllerSecurityPhase.WaitStartEncryption =>
                AcceptStartEncryption(payload, responses),
            _ => false,
        };
    }

    bool AcceptHandsetAuthentication(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (payload.Length != 5 || payload[0] != 0x19)
        {
            return false;
        }
        if (!payload[1..].SequenceEqual(_expectedHandsetResponse))
        {
            _securityPhase =
                BluetoothLegacyConnectionControllerSecurityPhase.AuthenticationFailed;
            return false;
        }
        _securityPhase =
            BluetoothLegacyConnectionControllerSecurityPhase.WaitEncryptionMode;
        responses.Add(CreateRecord(0x1e));
        return true;
    }

    bool AcceptEncryptionMode(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (!payload.SequenceEqual(new byte[] { 0x07, 0x0f }))
        {
            return false;
        }
        _securityPhase =
            BluetoothLegacyConnectionControllerSecurityPhase.WaitEncryptionKeySize;
        responses.Add(CreateRecord(0x06));
        return true;
    }

    bool AcceptEncryptionKeySize(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (!payload.SequenceEqual(new byte[] { 0x21, 0x05 }))
        {
            return false;
        }
        _securityPhase =
            BluetoothLegacyConnectionControllerSecurityPhase.WaitStartEncryption;
        responses.Add(CreateRecord(0x21, [0x05]));
        return true;
    }

    bool AcceptStartEncryption(
        ReadOnlySpan<byte> payload,
        List<byte[]> responses)
    {
        if (payload.Length != 17 || payload[0] != 0x23)
        {
            return false;
        }
        _securityPhase = BluetoothLegacyConnectionControllerSecurityPhase.Complete;
        responses.Add(CreateRecord(0x06));
        return true;
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
