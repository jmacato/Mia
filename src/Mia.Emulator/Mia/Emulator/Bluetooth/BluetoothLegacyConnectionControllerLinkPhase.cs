// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Bluetooth;

internal enum BluetoothLegacyConnectionControllerLinkPhase
{
    WaitInitialFeatureResponse,
    WaitFeatureRequest,
    WaitExtendedFeatureSequence,
    WaitCompletionRequest,
    WaitFinalAccepted,
    Complete,
}
