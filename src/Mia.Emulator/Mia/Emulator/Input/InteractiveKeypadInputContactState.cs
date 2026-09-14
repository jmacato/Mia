// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Input;

internal sealed class InteractiveKeypadInputContactState
{
    public bool PhysicallyPressed;
    public byte PrimaryObservations;
    public byte SecondaryObservations;
    public bool ReleaseRequested;
}
