// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Mia.App.Controls;

public sealed class MiaPhoneKeyControl : Control, ICustomHitTest
{
    const double BoundsPadding = 3;
    const double PressedOffset = 1.5;

    static readonly IBrush PressedOverlayBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.Parse("#78101721"), 0),
            new GradientStop(Color.Parse("#50192835"), 0.45),
            new GradientStop(Color.Parse("#24121d28"), 1),
        ],
    };
    static readonly IPen PressedEdgePen = new Pen(
        new SolidColorBrush(Color.Parse("#8c10171e")),
        1.25);
    static readonly IBrush PressedIconDarkenBrush =
        new SolidColorBrush(Color.Parse("#70000000"));
    Geometry _region = new RectangleGeometry();
    Geometry? _pressedIcon;
    MiaPhoneKey _key;
    bool _pressed;

    public MiaPhoneKeyControl()
    {
        MinWidth = 0;
        MinHeight = 0;
        Margin = new Thickness(0);
        Focusable = false;
        ClipToBounds = true;
        Configure(MiaPhoneKey.Yes);
    }

    public MiaPhoneKey Key
    {
        get => _key;
        set => Configure(value);
    }

    internal Rect PhoneBounds { get; private set; }

    internal bool ContainsPhonePoint(Point point) =>
        _region.FillContains(point);

    internal void SetPressed(bool value)
    {
        if (_pressed == value)
        {
            return;
        }

        _pressed = value;
        InvalidateVisual();
    }

    public bool HitTest(Point point)
    {
        Point phonePoint = new(
            point.X + PhoneBounds.X,
            point.Y + PhoneBounds.Y);
        return ContainsPhonePoint(phonePoint);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_pressed)
        {
            return;
        }

        DrawPressedState(context);
    }

    void DrawPressedState(DrawingContext context)
    {
        if (_pressedIcon is not null)
        {
            DrawPressedIcon(context, _pressedIcon);
        }
        else
        {
            DrawPressedOverlay(context);
        }
    }

    void DrawPressedIcon(DrawingContext context, Geometry icon)
    {
        using (context.PushTransform(Matrix.CreateTranslation(
                   -PhoneBounds.X,
                   -PhoneBounds.Y)))
        {
            context.DrawGeometry(
                PressedIconDarkenBrush,
                pen: null,
                icon);
        }
    }

    void DrawPressedOverlay(DrawingContext context)
    {
        using (context.PushTransform(Matrix.CreateTranslation(
                   -PhoneBounds.X,
                   -PhoneBounds.Y + PressedOffset)))
        using (context.PushGeometryClip(_region))
        {
            context.DrawGeometry(
                PressedOverlayBrush,
                PressedEdgePen,
                _region);
        }
    }

    internal static bool UsesIconOnlyPressedVisual(MiaPhoneKey key) =>
        key is MiaPhoneKey.Yes or MiaPhoneKey.NoPower;

    void Configure(MiaPhoneKey key)
    {
        _key = key;
        _region = CreateRegion(key);
        _pressedIcon = CreatePressedIcon(key);
        PhoneBounds = _region.Bounds.Inflate(BoundsPadding);
        Width = PhoneBounds.Width;
        Height = PhoneBounds.Height + PressedOffset;
        Canvas.SetLeft(this, PhoneBounds.X);
        Canvas.SetTop(this, PhoneBounds.Y);
        Tag = key;
    }

    static Geometry CreateRegion(MiaPhoneKey key) => key switch
    {
        MiaPhoneKey.Yes => MiaPhoneGeometry.CreateKey(0),
        MiaPhoneKey.NoPower => MiaPhoneGeometry.CreateKey(1),
        MiaPhoneKey.Options => MiaPhoneGeometry.CreateKey(2),
        MiaPhoneKey.Clear => MiaPhoneGeometry.CreateKey(7),
        MiaPhoneKey.Digit1 => MiaPhoneGeometry.CreateKey(3),
        MiaPhoneKey.Digit2 => MiaPhoneGeometry.CreateKey(12),
        MiaPhoneKey.Digit3 => MiaPhoneGeometry.CreateKey(8),
        MiaPhoneKey.Digit4 => MiaPhoneGeometry.CreateKey(4),
        MiaPhoneKey.Digit5 => MiaPhoneGeometry.CreateKey(13),
        MiaPhoneKey.Digit6 => MiaPhoneGeometry.CreateKey(9),
        MiaPhoneKey.Digit7 => MiaPhoneGeometry.CreateKey(5),
        MiaPhoneKey.Digit8 => MiaPhoneGeometry.CreateKey(14),
        MiaPhoneKey.Digit9 => MiaPhoneGeometry.CreateKey(10),
        MiaPhoneKey.Star => MiaPhoneGeometry.CreateKey(6),
        MiaPhoneKey.Digit0 => MiaPhoneGeometry.CreateKey(15),
        MiaPhoneKey.Hash => MiaPhoneGeometry.CreateKey(11),
        MiaPhoneKey.Up => CreateJoystickRegion(0),
        MiaPhoneKey.Right => CreateJoystickRegion(1),
        MiaPhoneKey.Down => CreateJoystickRegion(2),
        MiaPhoneKey.Left => CreateJoystickRegion(3),
        MiaPhoneKey.Joystick => MiaPhoneGeometry.CreateJoystickInner(),
        MiaPhoneKey.VolumeUp => CreateRockerRegion(top: true),
        MiaPhoneKey.VolumeDown => CreateRockerRegion(top: false),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    static CombinedGeometry CreateJoystickRegion(int direction)
    {
        Geometry outer = MiaPhoneGeometry.CreateJoystickOuter();
        Geometry inner = MiaPhoneGeometry.CreateJoystickInner();
        var ring = new CombinedGeometry(GeometryCombineMode.Xor, outer, inner);
        Rect bounds = outer.Bounds;
        Point center = bounds.Center;
        (Point first, Point second) = direction switch
        {
            0 => (bounds.TopLeft, bounds.TopRight),
            1 => (bounds.TopRight, bounds.BottomRight),
            2 => (bounds.BottomRight, bounds.BottomLeft),
            _ => (bounds.BottomLeft, bounds.TopLeft),
        };
        return new CombinedGeometry(
            GeometryCombineMode.Intersect,
            ring,
            CreateWedge(center, first, second));
    }

    static CombinedGeometry CreateRockerRegion(bool top)
    {
        Geometry rocker = MiaPhoneGeometry.CreateSideRocker();
        Rect bounds = rocker.Bounds;
        double middle = bounds.Center.Y;
        Rect half = top
            ? new Rect(
                bounds.X - 1,
                bounds.Y - 1,
                bounds.Width + 2,
                middle - bounds.Y + 1)
            : new Rect(
                bounds.X - 1,
                middle,
                bounds.Width + 2,
                bounds.Bottom - middle + 1);
        return new CombinedGeometry(
            GeometryCombineMode.Intersect,
            rocker,
            new RectangleGeometry(half));
    }

    static StreamGeometry CreateWedge(Point center, Point first, Point second)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(center, isFilled: true);
        context.LineTo(first);
        context.LineTo(second);
        context.EndFigure(isClosed: true);
        return geometry;
    }

    static Geometry? CreatePressedIcon(MiaPhoneKey key) => key switch
    {
        MiaPhoneKey.Yes => MiaPhoneGeometry.CreateLegend4(),
        MiaPhoneKey.NoPower => new CombinedGeometry(
            GeometryCombineMode.Union,
            MiaPhoneGeometry.CreateLegend5(),
            MiaPhoneGeometry.CreateLegend6()),
        _ => null,
    };
}
