// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Input;

namespace Mia.App.Controls;

internal static class MiaPhoneKeys
{
    internal static MiaPhoneKey? MapJoystickPress(
        Point point,
        Point center,
        double hitRadius,
        double centerDeadZoneRadius)
    {
        double horizontal = point.X - center.X;
        double vertical = point.Y - center.Y;
        double distanceSquared = horizontal * horizontal + vertical * vertical;
        return distanceSquared <= hitRadius * hitRadius
            ? MapJoystickDrag(
                horizontal,
                vertical,
                distanceSquared,
                centerDeadZoneRadius)
            : null;
    }

    internal static MiaPhoneKey MapJoystickDrag(
        Point point,
        Point center,
        double centerDeadZoneRadius)
    {
        double horizontal = point.X - center.X;
        double vertical = point.Y - center.Y;
        double distanceSquared = horizontal * horizontal + vertical * vertical;
        return MapJoystickDrag(
            horizontal,
            vertical,
            distanceSquared,
            centerDeadZoneRadius);
    }

    static MiaPhoneKey MapJoystickDrag(
        double horizontal,
        double vertical,
        double distanceSquared,
        double centerDeadZoneRadius)
    {
        if (distanceSquared <= centerDeadZoneRadius * centerDeadZoneRadius)
        {
            return MiaPhoneKey.Joystick;
        }

        if (Math.Abs(horizontal) > Math.Abs(vertical))
        {
            return horizontal < 0
                ? MiaPhoneKey.Left
                : MiaPhoneKey.Right;
        }

        return vertical < 0
            ? MiaPhoneKey.Up
            : MiaPhoneKey.Down;
    }

    internal static MiaPhoneKeypadContact GetContact(MiaPhoneKey key) => key switch
    {
        MiaPhoneKey.Yes => new(0x0d, 0x01),
        MiaPhoneKey.Up => new(0x0b, 0x10, 0x07),
        MiaPhoneKey.NoPower => new(0x0f, 0x01, Power: true),
        MiaPhoneKey.Left => new(0x0d, 0x10, 0x0b),
        MiaPhoneKey.Joystick => new(0x0f, 0x08),
        MiaPhoneKey.Right => new(0x0e, 0x10, 0x07),
        MiaPhoneKey.Options => new(0x0d, 0x02),
        MiaPhoneKey.Down => new(0x0e, 0x10, 0x0d),
        MiaPhoneKey.Clear => new(0x0f, 0x02),
        MiaPhoneKey.Digit1 => new(0x07, 0x01),
        MiaPhoneKey.Digit2 => new(0x0b, 0x01),
        MiaPhoneKey.Digit3 => new(0x0e, 0x01),
        MiaPhoneKey.Digit4 => new(0x07, 0x02),
        MiaPhoneKey.Digit5 => new(0x0b, 0x02),
        MiaPhoneKey.Digit6 => new(0x0e, 0x02),
        MiaPhoneKey.Digit7 => new(0x07, 0x04),
        MiaPhoneKey.Digit8 => new(0x0b, 0x04),
        MiaPhoneKey.Digit9 => new(0x0e, 0x04),
        MiaPhoneKey.Star => new(0x07, 0x08),
        MiaPhoneKey.Digit0 => new(0x0b, 0x08),
        MiaPhoneKey.Hash => new(0x0e, 0x08),
        MiaPhoneKey.VolumeUp => new(
            0x0d,
            0x04),
        MiaPhoneKey.VolumeDown => new(
            0x0d,
            0x08),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    internal static bool TryMapKeyboard(
        Key key,
        KeyModifiers modifiers,
        out MiaPhoneKey phoneKey)
    {
        MiaPhoneKey? mapped = key switch
        {
            Key.Left => MiaPhoneKey.Left,
            Key.Up => MiaPhoneKey.Up,
            Key.Right => MiaPhoneKey.Right,
            Key.Down => MiaPhoneKey.Down,
            Key.Return or Key.Enter or Key.Space => MiaPhoneKey.Joystick,
            Key.Y => MiaPhoneKey.Yes,
            Key.O => MiaPhoneKey.Options,
            Key.N or Key.Escape => MiaPhoneKey.NoPower,
            Key.C or Key.Back or Key.Delete => MiaPhoneKey.Clear,
            Key.VolumeUp or Key.PageUp => MiaPhoneKey.VolumeUp,
            Key.VolumeDown or Key.PageDown => MiaPhoneKey.VolumeDown,
            Key.D0 or Key.NumPad0 => MiaPhoneKey.Digit0,
            Key.D1 or Key.NumPad1 => MiaPhoneKey.Digit1,
            Key.D2 or Key.NumPad2 => MiaPhoneKey.Digit2,
            Key.D3 or Key.NumPad3 when !modifiers.HasFlag(KeyModifiers.Shift) =>
                MiaPhoneKey.Digit3,
            Key.D4 or Key.NumPad4 => MiaPhoneKey.Digit4,
            Key.D5 or Key.NumPad5 => MiaPhoneKey.Digit5,
            Key.D6 or Key.NumPad6 => MiaPhoneKey.Digit6,
            Key.D7 or Key.NumPad7 => MiaPhoneKey.Digit7,
            Key.D8 or Key.NumPad8 when !modifiers.HasFlag(KeyModifiers.Shift) =>
                MiaPhoneKey.Digit8,
            Key.D9 or Key.NumPad9 => MiaPhoneKey.Digit9,
            Key.D8 when modifiers.HasFlag(KeyModifiers.Shift) => MiaPhoneKey.Star,
            Key.Multiply => MiaPhoneKey.Star,
            Key.D3 when modifiers.HasFlag(KeyModifiers.Shift) => MiaPhoneKey.Hash,
            Key.OemPlus or Key.Add or Key.Divide => MiaPhoneKey.Hash,
            _ => null,
        };
        phoneKey = mapped.GetValueOrDefault();
        return mapped.HasValue;
    }
}
