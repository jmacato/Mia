// SPDX-License-Identifier: MIT

using Mia.App.Controls;
using Xunit;

namespace Mia.App.Tests;

public sealed class MiaPhoneLedVisualTests
{
    [Theory]
    [InlineData(false, false, nameof(MiaPhoneLedColor.Off))]
    [InlineData(true, false, nameof(MiaPhoneLedColor.Red))]
    [InlineData(false, true, nameof(MiaPhoneLedColor.Green))]
    [InlineData(true, true, nameof(MiaPhoneLedColor.Amber))]
    public void ResolvesTheLeftBicolorLamp(
        bool red,
        bool green,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<MiaPhoneLedColor>(expected),
            MiaPhoneFace.ResolveLeftLedColor(red, green));
    }

    [Theory]
    [InlineData(false, nameof(MiaPhoneLedColor.Off))]
    [InlineData(true, nameof(MiaPhoneLedColor.Blue))]
    public void ResolvesTheRightBlueLamp(
        bool blue,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<MiaPhoneLedColor>(expected),
            MiaPhoneFace.ResolveRightLedColor(blue));
    }

    [Theory]
    [InlineData(false, 0.18)]
    [InlineData(true, 1.0)]
    public void ResolvesLcdBacklightOpacity(bool on, double expected)
    {
        Assert.Equal(expected, LcdControl.ResolveBacklightOpacity(on));
    }

    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 1.0)]
    public void ResolvesKeypadLegendBacklightOpacity(bool on, double expected)
    {
        Assert.Equal(
            expected,
            MiaPhoneFace.ResolveKeypadBacklightOpacity(on));
    }

    [Theory]
    [InlineData(nameof(MiaPhoneKey.Yes), true)]
    [InlineData(nameof(MiaPhoneKey.NoPower), true)]
    [InlineData(nameof(MiaPhoneKey.Options), false)]
    [InlineData(nameof(MiaPhoneKey.Digit5), false)]
    public void YesAndNoUseIconOnlyPressedFeedback(
        string keyName,
        bool expected)
    {
        Assert.Equal(
            expected,
            MiaPhoneKeyControl.UsesIconOnlyPressedVisual(
                Enum.Parse<MiaPhoneKey>(keyName)));
    }
}
