// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

internal enum BluetoothLegacyConnectionControllerSecurityPhase
{
    None,
    ReadyToChallenge,
    WaitHandsetAuthentication,
    WaitEncryptionMode,
    WaitEncryptionKeySize,
    WaitStartEncryption,
    Complete,
    AuthenticationFailed,
}
