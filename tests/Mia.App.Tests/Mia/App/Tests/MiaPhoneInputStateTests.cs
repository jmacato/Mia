// SPDX-License-Identifier: MIT

using Mia.App.Controls;
using Xunit;

namespace Mia.App.Tests;

public sealed class MiaPhoneInputStateTests
{
    [Fact]
    public void InputEnabledIsBindableFromHostAxaml()
    {
        Assert.Equal(
            nameof(MiaPhoneFace.InputEnabled),
            MiaPhoneFace.InputEnabledProperty.Name);
    }

    [Fact]
    public void SeparatePointersHoldSeparateKeys()
    {
        MiaPhoneInputState state = new();

        MiaPhoneInputPressChange first =
            state.PressPointer(10, MiaPhoneKey.Digit1);
        MiaPhoneInputPressChange second =
            state.PressPointer(11, MiaPhoneKey.Digit2);

        Assert.True(first.KeyBecameActive);
        Assert.True(second.KeyBecameActive);
        Assert.True(state.IsActive(MiaPhoneKey.Digit1));
        Assert.True(state.IsActive(MiaPhoneKey.Digit2));
        Assert.True(state.ReleasePointer(10).KeyBecameInactive);
        Assert.False(state.IsActive(MiaPhoneKey.Digit1));
        Assert.True(state.IsActive(MiaPhoneKey.Digit2));
    }

    [Fact]
    public void KeyStaysDownUntilItsLastPointerIsReleased()
    {
        MiaPhoneInputState state = new();

        Assert.True(state.PressPointer(20, MiaPhoneKey.Joystick).KeyBecameActive);
        Assert.False(state.PressPointer(21, MiaPhoneKey.Joystick).KeyBecameActive);
        Assert.False(state.ReleasePointer(20).KeyBecameInactive);
        Assert.True(state.IsActive(MiaPhoneKey.Joystick));
        Assert.True(state.ReleasePointer(21).KeyBecameInactive);
        Assert.False(state.IsActive(MiaPhoneKey.Joystick));
    }

    [Fact]
    public void PointerAndKeyboardSourcesDoNotReleaseEachOther()
    {
        MiaPhoneInputState state = new();
        state.PressPointer(30, MiaPhoneKey.Digit0);
        state.PressKeyboard(100, MiaPhoneKey.Digit0);
        state.PressKeyboard(101, MiaPhoneKey.Digit0);

        Assert.False(state.ReleasePointer(30).KeyBecameInactive);
        Assert.False(state.ReleaseKeyboard(100).KeyBecameInactive);
        Assert.True(state.IsActive(MiaPhoneKey.Digit0));
        Assert.True(state.ReleaseKeyboard(101).KeyBecameInactive);
        Assert.False(state.IsActive(MiaPhoneKey.Digit0));
    }

    [Fact]
    public void DuplicatePressFromOneSourceIsIdempotent()
    {
        MiaPhoneInputState state = new();

        Assert.True(state.PressPointer(40, MiaPhoneKey.Clear).SourceChanged);
        MiaPhoneInputPressChange duplicate =
            state.PressPointer(40, MiaPhoneKey.Clear);

        Assert.False(duplicate.SourceChanged);
        Assert.False(duplicate.KeyBecameActive);
        Assert.True(state.ReleasePointer(40).KeyBecameInactive);
    }

    [Fact]
    public void ReassignedSourceReleasesOnlyItsPreviousKey()
    {
        MiaPhoneInputState state = new();
        state.PressPointer(50, MiaPhoneKey.Left);
        state.PressPointer(51, MiaPhoneKey.Left);

        MiaPhoneInputPressChange change =
            state.PressPointer(50, MiaPhoneKey.Right);

        Assert.Equal(MiaPhoneKey.Left, change.PreviousKey);
        Assert.False(change.PreviousKeyBecameInactive);
        Assert.True(change.KeyBecameActive);
        Assert.True(state.IsActive(MiaPhoneKey.Left));
        Assert.True(state.IsActive(MiaPhoneKey.Right));
    }

    [Fact]
    public void KeyboardReleaseUsesOriginalPhysicalSourceMapping()
    {
        MiaPhoneInputState state = new();
        state.PressKeyboard(80, MiaPhoneKey.Star);

        MiaPhoneInputReleaseChange release = state.ReleaseKeyboard(80);

        Assert.True(release.Found);
        Assert.Equal(MiaPhoneKey.Star, release.Key);
        Assert.True(release.KeyBecameInactive);
    }

    [Fact]
    public void KeyboardRepeatCanFollowModifierMappingChange()
    {
        MiaPhoneInputState state = new();
        state.PressKeyboard(80, MiaPhoneKey.Star);

        MiaPhoneInputPressChange change =
            state.PressKeyboard(80, MiaPhoneKey.Digit8);

        Assert.Equal(MiaPhoneKey.Star, change.PreviousKey);
        Assert.True(change.PreviousKeyBecameInactive);
        Assert.True(change.KeyBecameActive);
        Assert.False(state.IsActive(MiaPhoneKey.Star));
        Assert.True(state.IsActive(MiaPhoneKey.Digit8));
    }

    [Fact]
    public void ClearRemovesAllPointerAndKeyboardOwners()
    {
        MiaPhoneInputState state = new();
        state.PressPointer(60, MiaPhoneKey.Digit4);
        state.PressKeyboard(61, MiaPhoneKey.Digit5);

        state.Clear();

        Assert.Empty(state.ActiveKeys);
        Assert.False(state.ReleasePointer(60).Found);
        Assert.False(state.ReleaseKeyboard(61).Found);
    }
}
