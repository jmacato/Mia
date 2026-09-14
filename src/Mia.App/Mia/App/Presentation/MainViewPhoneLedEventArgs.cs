// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public sealed class MainViewPhoneLedEventArgs(
    bool leftRed,
    bool leftGreen,
    bool rightBlue,
    bool backlightsOn) : EventArgs
{
    public bool LeftRed { get; } = leftRed;
    public bool LeftGreen { get; } = leftGreen;
    public bool RightBlue { get; } = rightBlue;
    public bool BacklightsOn { get; } = backlightsOn;
}
