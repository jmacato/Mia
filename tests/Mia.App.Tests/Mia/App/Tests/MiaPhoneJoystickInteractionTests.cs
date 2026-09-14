// SPDX-License-Identifier: MIT

using Avalonia;
using Mia.App.Controls;
using Xunit;

namespace Mia.App.Tests;

public sealed class MiaPhoneJoystickInteractionTests
{
    const double HitRadius = 42;
    const double CenterDeadZoneRadius = 14;

    static readonly Point Center = new(100, 100);

    [Theory]
    [InlineData(100, 100, nameof(MiaPhoneKey.Joystick))]
    [InlineData(100, 86, nameof(MiaPhoneKey.Joystick))]
    [InlineData(100, 75, nameof(MiaPhoneKey.Up))]
    [InlineData(125, 100, nameof(MiaPhoneKey.Right))]
    [InlineData(100, 125, nameof(MiaPhoneKey.Down))]
    [InlineData(75, 100, nameof(MiaPhoneKey.Left))]
    public void MapsExpandedPressAreaToFiveJoystickContacts(
        double x,
        double y,
        string expected)
    {
        MiaPhoneKey? actual = MiaPhoneKeys.MapJoystickPress(
            new Point(x, y),
            Center,
            HitRadius,
            CenterDeadZoneRadius);

        Assert.Equal(Enum.Parse<MiaPhoneKey>(expected), actual);
    }

    [Fact]
    public void RejectsPressOutsideExpandedHitArea()
    {
        MiaPhoneKey? actual = MiaPhoneKeys.MapJoystickPress(
            new Point(143, 100),
            Center,
            HitRadius,
            CenterDeadZoneRadius);

        Assert.Null(actual);
    }

    [Theory]
    [InlineData(100, -200, nameof(MiaPhoneKey.Up))]
    [InlineData(400, 100, nameof(MiaPhoneKey.Right))]
    [InlineData(100, 400, nameof(MiaPhoneKey.Down))]
    [InlineData(-200, 100, nameof(MiaPhoneKey.Left))]
    public void CapturedDragRemainsDirectionalOutsideInitialHitArea(
        double x,
        double y,
        string expected)
    {
        MiaPhoneKey actual = MiaPhoneKeys.MapJoystickDrag(
            new Point(x, y),
            Center,
            CenterDeadZoneRadius);

        Assert.Equal(Enum.Parse<MiaPhoneKey>(expected), actual);
    }
}
