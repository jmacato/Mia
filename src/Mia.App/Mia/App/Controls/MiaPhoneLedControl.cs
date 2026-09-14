// SPDX-License-Identifier: MIT

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mia.App.Controls;

public sealed class MiaPhoneLedControl : Control
{
    const double BoundsPadding = 8;
    const double GlowWidth = 7;

    static readonly IBrush RedFillBrush = CreateFillBrush(
        "#ffffb0b5",
        "#fff12d42",
        "#ff8e0719");
    static readonly IBrush GreenFillBrush = CreateFillBrush(
        "#ffc5ffd3",
        "#ff2bea64",
        "#ff08752b");
    static readonly IBrush AmberFillBrush = CreateFillBrush(
        "#fffff0aa",
        "#ffffad20",
        "#ffa34a00");
    static readonly IBrush BlueFillBrush = CreateFillBrush(
        "#ffc6e1ff",
        "#ff318dff",
        "#ff0a419c");
    static readonly IBrush RedGlowBrush =
        new SolidColorBrush(Color.Parse("#70ff3048"));
    static readonly IBrush GreenGlowBrush =
        new SolidColorBrush(Color.Parse("#702ff16b"));
    static readonly IBrush AmberGlowBrush =
        new SolidColorBrush(Color.Parse("#70ffb123"));
    static readonly IBrush BlueGlowBrush =
        new SolidColorBrush(Color.Parse("#70358fff"));
    static readonly IPen EdgePen = new Pen(
        new SolidColorBrush(Color.Parse("#dcffffff")),
        1.1);

    Geometry _region = new RectangleGeometry();
    MiaPhoneLedSide _side;
    MiaPhoneLedColor _color;

    public MiaPhoneLedControl()
    {
        MinWidth = 0;
        MinHeight = 0;
        Margin = new Thickness(0);
        Focusable = false;
        IsHitTestVisible = false;
        ClipToBounds = true;
        Configure(MiaPhoneLedSide.Left);
    }

    public MiaPhoneLedSide Side
    {
        get => _side;
        set => Configure(value);
    }

    internal Rect PhoneBounds { get; private set; }

    internal MiaPhoneLedColor LedColor => _color;

    internal void SetColor(MiaPhoneLedColor color)
    {
        if (_color == color)
        {
            return;
        }

        _color = color;
        InvalidateVisual();
    }

    void Configure(MiaPhoneLedSide side)
    {
        _side = side;
        _region = side == MiaPhoneLedSide.Left
            ? MiaPhoneGeometry.CreateTopCornerLeft()
            : MiaPhoneGeometry.CreateTopCornerRight();
        PhoneBounds = _region.Bounds.Inflate(BoundsPadding);
        Width = PhoneBounds.Width;
        Height = PhoneBounds.Height;
        Canvas.SetLeft(this, PhoneBounds.X);
        Canvas.SetTop(this, PhoneBounds.Y);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        if (_color == MiaPhoneLedColor.Off)
        {
            return;
        }

        (IBrush fill, IBrush glow) = _color switch
        {
            MiaPhoneLedColor.Red => (RedFillBrush, RedGlowBrush),
            MiaPhoneLedColor.Green => (GreenFillBrush, GreenGlowBrush),
            MiaPhoneLedColor.Amber => (AmberFillBrush, AmberGlowBrush),
            MiaPhoneLedColor.Blue => (BlueFillBrush, BlueGlowBrush),
            _ => throw new InvalidOperationException(
                $"Unsupported phone LED color {_color}."),
        };

        using (context.PushTransform(Matrix.CreateTranslation(
                   -PhoneBounds.X,
                   -PhoneBounds.Y)))
        {
            context.DrawGeometry(null, new Pen(glow, GlowWidth), _region);
            context.DrawGeometry(fill, EdgePen, _region);
        }
    }

    static LinearGradientBrush CreateFillBrush(
        string highlight,
        string body,
        string shadow) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.2, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.8, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse(highlight), 0),
                new GradientStop(Color.Parse(body), 0.48),
                new GradientStop(Color.Parse(shadow), 1),
            ],
        };
}
