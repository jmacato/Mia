// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Mia.App.Presentation;
using SvgControl = Avalonia.Svg.Skia.Svg;

namespace Mia.App.Controls;

public sealed partial class MiaPhoneFace : UserControl
{
    const double JoystickHitDiameterScale = 1.8;
    const double JoystickCenterDeadZoneDiameterScale = 0.84;

    public static readonly DirectProperty<MiaPhoneFace, bool> InputEnabledProperty =
        AvaloniaProperty.RegisterDirect<MiaPhoneFace, bool>(
            nameof(InputEnabled),
            face => face.InputEnabled,
            (face, value) => face.InputEnabled = value);

    readonly MiaPhoneInputState _inputState = new();
    readonly Dictionary<MiaPhoneKey, MiaPhoneKeyControl> _keyControls;
    readonly HashSet<long> _joystickPointers = [];
    readonly LcdControl _lcd;
    readonly MiaPhoneLedControl _leftLed;
    readonly MiaPhoneLedControl _rightLed;
    readonly SvgControl _keypadBacklight;
    readonly Point _joystickCenter;
    readonly double _joystickHitRadius;
    readonly double _joystickCenterDeadZoneRadius;
    bool _inputEnabled;

    public MiaPhoneFace()
    {
        InitializeComponent();
        _lcd = this.FindControl<LcdControl>("LcdDisplay")!;
        _leftLed = this.FindControl<MiaPhoneLedControl>("LeftLed")!;
        _rightLed = this.FindControl<MiaPhoneLedControl>("RightLed")!;
        _keypadBacklight = this.FindControl<SvgControl>("KeypadBacklight")!;
        _keyControls = this.GetLogicalDescendants()
            .OfType<MiaPhoneKeyControl>()
            .ToDictionary(control => control.Key);

        Rect joystickBounds = MiaPhoneGeometry.CreateJoystickOuter().Bounds;
        Rect joystickInnerBounds = MiaPhoneGeometry.CreateJoystickInner().Bounds;
        _joystickCenter = joystickBounds.Center;
        _joystickHitRadius =
            Math.Max(joystickBounds.Width, joystickBounds.Height) *
            JoystickHitDiameterScale / 2;
        _joystickCenterDeadZoneRadius =
            Math.Max(joystickInnerBounds.Width, joystickInnerBounds.Height) *
            JoystickCenterDeadZoneDiameterScale / 2;
        AttachInputHandlers();
    }

    internal event Action<MiaPhoneKeypadContact, bool>? ContactChanged;
    internal LcdControl Lcd => _lcd;

    public bool InputEnabled
    {
        get => _inputEnabled;
        set
        {
            if (!SetAndRaise(InputEnabledProperty, ref _inputEnabled, value))
            {
                return;
            }

            if (!value)
            {
                ReleaseAllInput();
            }

            foreach (MiaPhoneKeyControl control in _keyControls.Values)
            {
                control.IsEnabled = value;
            }
        }
    }

    internal void PressKeyboard(int sourceKey, MiaPhoneKey key)
    {
        if (_inputEnabled)
        {
            ApplyPressChange(_inputState.PressKeyboard(sourceKey, key), key);
        }
    }

    internal bool ReleaseKeyboard(int sourceKey)
    {
        MiaPhoneInputReleaseChange change = _inputState.ReleaseKeyboard(sourceKey);
        if (!change.Found)
        {
            return false;
        }

        if (change.KeyBecameInactive)
        {
            SetContact(change.Key, false);
        }

        UpdateKeyVisual(change.Key);
        return true;
    }

    internal void ReleaseAllInput()
    {
        MiaPhoneKey[] activeKeys = _inputState.ActiveKeys.ToArray();
        _inputState.Clear();
        _joystickPointers.Clear();
        foreach (MiaPhoneKey key in activeKeys)
        {
            SetContact(key, false);
            UpdateKeyVisual(key);
        }
    }

    internal void SetLeds(MainViewPhoneLedEventArgs state)
    {
        _leftLed.SetColor(ResolveLeftLedColor(state.LeftRed, state.LeftGreen));
        _rightLed.SetColor(ResolveRightLedColor(state.RightBlue));
        _lcd.SetBacklight(state.BacklightsOn);
        _keypadBacklight.Opacity = ResolveKeypadBacklightOpacity(
            state.BacklightsOn);
    }

    internal static double ResolveKeypadBacklightOpacity(bool on) => on ? 1 : 0;

    internal static MiaPhoneLedColor ResolveLeftLedColor(bool red, bool green) =>
        (red, green) switch
        {
            (true, true) => MiaPhoneLedColor.Amber,
            (true, false) => MiaPhoneLedColor.Red,
            (false, true) => MiaPhoneLedColor.Green,
            _ => MiaPhoneLedColor.Off,
        };

    internal static MiaPhoneLedColor ResolveRightLedColor(bool blue) =>
        blue ? MiaPhoneLedColor.Blue : MiaPhoneLedColor.Off;

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void AttachInputHandlers()
    {
        AddHandler(PointerPressedEvent, OnJoystickPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnKeyPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnJoystickPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnAnyPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnAnyPointerCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    void OnKeyPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.Handled || !_inputEnabled ||
            eventArgs.Source is not MiaPhoneKeyControl control)
        {
            return;
        }

        eventArgs.PreventGestureRecognition();
        ApplyPressChange(
            _inputState.PressPointer(eventArgs.Pointer.Id, control.Key),
            control.Key);
        eventArgs.Pointer.Capture(control);
        eventArgs.Handled = true;
    }

    void OnJoystickPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.Handled || !_inputEnabled)
        {
            return;
        }

        Point point = eventArgs.GetPosition(this);
        MiaPhoneKey? mappedKey = MiaPhoneKeys.MapJoystickPress(
                point,
                _joystickCenter,
                _joystickHitRadius,
                _joystickCenterDeadZoneRadius);
        if (IsPointOnAdjacentKey(point) || mappedKey is not { } key)
        {
            return;
        }

        eventArgs.PreventGestureRecognition();
        _joystickPointers.Add(eventArgs.Pointer.Id);
        ApplyPressChange(_inputState.PressPointer(eventArgs.Pointer.Id, key), key);
        eventArgs.Pointer.Capture(this);
        eventArgs.Handled = true;
    }

    void OnJoystickPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_joystickPointers.Contains(eventArgs.Pointer.Id))
        {
            return;
        }

        MiaPhoneKey key = MiaPhoneKeys.MapJoystickDrag(
            eventArgs.GetPosition(this),
            _joystickCenter,
            _joystickCenterDeadZoneRadius);
        MiaPhoneInputPressChange change =
            _inputState.PressPointer(eventArgs.Pointer.Id, key);
        if (change.SourceChanged)
        {
            ApplyPressChange(change, key);
        }

        eventArgs.Handled = true;
    }

    bool IsPointOnAdjacentKey(Point point) =>
        _keyControls[MiaPhoneKey.Yes].ContainsPhonePoint(point) ||
        _keyControls[MiaPhoneKey.NoPower].ContainsPhonePoint(point) ||
        _keyControls[MiaPhoneKey.Options].ContainsPhonePoint(point) ||
        _keyControls[MiaPhoneKey.Clear].ContainsPhonePoint(point);

    void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!ReleasePointer(eventArgs.Pointer.Id))
        {
            return;
        }

        eventArgs.PreventGestureRecognition();
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }

    void OnAnyPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs eventArgs) =>
        ReleasePointer(eventArgs.Pointer.Id);

    void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs eventArgs) => ReleaseAllInput();

    void ApplyPressChange(MiaPhoneInputPressChange change, MiaPhoneKey key)
    {
        ApplyPreviousKeyChange(change);

        if (change.KeyBecameActive)
        {
            SetContact(key, true);
        }

        UpdateKeyVisual(key);
    }

    void ApplyPreviousKeyChange(MiaPhoneInputPressChange change)
    {
        if (change.PreviousKey is not { } previousKey)
        {
            return;
        }

        if (change.PreviousKeyBecameInactive)
        {
            SetContact(previousKey, false);
        }

        UpdateKeyVisual(previousKey);
    }

    bool ReleasePointer(long pointerId)
    {
        _joystickPointers.Remove(pointerId);
        MiaPhoneInputReleaseChange change = _inputState.ReleasePointer(pointerId);
        if (!change.Found)
        {
            return false;
        }

        if (change.KeyBecameInactive)
        {
            SetContact(change.Key, false);
        }

        UpdateKeyVisual(change.Key);
        return true;
    }

    void SetContact(MiaPhoneKey key, bool pressed) =>
        ContactChanged?.Invoke(MiaPhoneKeys.GetContact(key), pressed);

    void UpdateKeyVisual(MiaPhoneKey key) =>
        _keyControls[key].SetPressed(_inputState.IsActive(key));
}
