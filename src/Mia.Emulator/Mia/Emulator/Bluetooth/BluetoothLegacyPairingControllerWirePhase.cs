// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

internal enum BluetoothLegacyPairingControllerWirePhase
{
    None,
    WaitInitialFeatureResponse,
    WaitInitializationRandom,
    WaitInitializationAccepted,
    WaitCombinationKey,
    WaitAuthenticationChallenge,
    WaitHandsetAuthentication,
    WaitHandsetAuthenticationAccepted,
    WaitEncryptionMode,
    WaitEncryptionKeySize,
    WaitStartEncryption,
    WaitFeatureRequest,
    WaitExtendedFeatureSequence,
    WaitCompletionRequest,
    WaitFinalAccepted,
    Complete,
    AuthenticationFailed,
}
