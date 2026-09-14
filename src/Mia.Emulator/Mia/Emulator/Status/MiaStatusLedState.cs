// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Status;

/// <summary>
/// User-visible states of the two top indicators described by the Mia
/// whitepaper. These are deliberately named for their recovered semantic
/// sources rather than for an unproven package pin.
/// </summary>
internal readonly record struct MiaStatusLedState(
    bool NetworkGreen,
    bool BluetoothBlue);
