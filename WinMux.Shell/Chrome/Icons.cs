using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

// System.IO.Path arrives through ImplicitUsings. Third name collision in this shell after Rect
// and TabStrip — assume any short shape-or-geometry name has a namesake.
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The toolbar's icons, drawn as geometry.
///
/// Not a symbol font. Segoe Fluent Icons is the right look on Windows 11, but a wrong codepoint
/// renders as a hollow box and a machine without the font renders every icon as one — a failure
/// that looks like a broken application and cannot be caught by any test we run. These are a dozen
/// lines of path data each, they scale with DPI, and they cannot be missing.
///
/// Each is drawn on a 16x16 grid and inherits the button's <c>Foreground</c>, so icons follow the
/// system theme and the hover state along with their labels.
/// </summary>
internal static class Icons
{
    private const double Size = 16;
    private const double Stroke = 1.3;

    /// <summary>A pane split into two columns, with the new pane on the right.</summary>
    public static Control SplitColumns() => Draw(
        Outline(),
        new Line { StartPoint = new Point(8, 2.5), EndPoint = new Point(8, 13.5) },
        Fill(new Rect(8.75, 3.25, 4, 9.5)));

    /// <summary>A pane split into two rows, with the new pane below.</summary>
    public static Control SplitRows() => Draw(
        Outline(),
        new Line { StartPoint = new Point(2.5, 8), EndPoint = new Point(13.5, 8) },
        Fill(new Rect(3.25, 8.75, 9.5, 4)));

    /// <summary>A pane with a tab strip along its top edge.</summary>
    public static Control TabGroup() => Draw(
        Outline(),
        new Line { StartPoint = new Point(2.5, 6), EndPoint = new Point(13.5, 6) },
        Fill(new Rect(3.25, 3.25, 4, 2)));

    /// <summary>A terminal prompt.</summary>
    public static Control Terminal() => Draw(
        Outline(),
        new Polyline { Points = { new Point(5, 6.5), new Point(7.5, 8.5), new Point(5, 10.5) } },
        new Line { StartPoint = new Point(9, 10.5), EndPoint = new Point(11.5, 10.5) });

    /// <summary>A folder.</summary>
    public static Control Folder() => Draw(
        new ShapePath
        {
            Data = Geometry.Parse(
                "M 2.5,4.5 A 1,1 0 0 1 3.5,3.5 L 6.3,3.5 L 7.8,5.2 L 12.5,5.2 " +
                "A 1,1 0 0 1 13.5,6.2 L 13.5,11.5 A 1,1 0 0 1 12.5,12.5 " +
                "L 3.5,12.5 A 1,1 0 0 1 2.5,11.5 Z"),
        });

    /// <summary>A plus.</summary>
    public static Control Add() => Draw(
        new Line { StartPoint = new Point(8, 3.5), EndPoint = new Point(8, 12.5) },
        new Line { StartPoint = new Point(3.5, 8), EndPoint = new Point(12.5, 8) });

    /// <summary>A cross, for closing.</summary>
    public static Control Close(double size = 10) => Draw(
        size,
        new Line { StartPoint = new Point(4.5, 4.5), EndPoint = new Point(11.5, 11.5) },
        new Line { StartPoint = new Point(11.5, 4.5), EndPoint = new Point(4.5, 11.5) });

    /// <summary>
    /// A drawing pin seen from the side, leaning as Windows 11 draws its own: the head across the
    /// top, the shaft down to a point. Upright it reads as an exclamation mark at 10px.
    /// </summary>
    public static Control Pin(double size = 10) => Draw(
        size,
        new Line { StartPoint = new Point(9.5, 2.5), EndPoint = new Point(13.5, 6.5) },
        new ShapePath { Data = Geometry.Parse("M 10.5,3.5 L 7,7 L 4.5,8 L 8,11.5 L 9,9 L 12.5,5.5 Z") },
        new Line { StartPoint = new Point(6.5, 9.5), EndPoint = new Point(3, 13) });

    /// <summary>A floppy-disk save.</summary>
    public static Control Save() => Draw(
        new ShapePath
        {
            Data = Geometry.Parse(
                "M 3.5,3.5 L 10.5,3.5 L 12.5,5.5 L 12.5,12.5 L 3.5,12.5 Z"),
        },
        new ShapePath { Data = Geometry.Parse("M 5.5,3.5 L 5.5,6.5 L 10,6.5 L 10,3.5") },
        Fill(new Rect(5.5, 8.5, 5, 4)));

    /// <summary>A list, for the command palette.</summary>
    public static Control Commands() => Draw(
        new Line { StartPoint = new Point(3, 5), EndPoint = new Point(13, 5) },
        new Line { StartPoint = new Point(3, 8), EndPoint = new Point(13, 8) },
        new Line { StartPoint = new Point(3, 11), EndPoint = new Point(9, 11) });

    /// <summary>A downward chevron, for a dropdown.</summary>
    public static Control Chevron() => Draw(
        10,
        new Polyline { Points = { new Point(5, 6.5), new Point(8, 9.5), new Point(11, 6.5) } });

    /// <summary>An upward chevron, for the opposite direction.</summary>
    public static Control ChevronUp() => Draw(
        10,
        new Polyline { Points = { new Point(5, 9.5), new Point(8, 6.5), new Point(11, 9.5) } });

    /// <summary>A gear, for settings.</summary>
    public static Control Settings() => Draw(
        new Ellipse
        {
            Width = 4.6, Height = 4.6,
            Margin = new Thickness(5.7, 5.7, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        },
        new ShapePath
        {
            Data = Geometry.Parse(
                "M 8,2.4 L 9.3,3.6 L 11,3.2 L 11.7,4.8 L 13.1,5.4 L 12.8,7.1 " +
                "L 13.9,8.4 L 12.8,9.7 L 13.1,11.4 L 11.7,12 L 11,13.6 L 9.3,13.2 " +
                "L 8,14.4 L 6.7,13.2 L 5,13.6 L 4.3,12 L 2.9,11.4 L 3.2,9.7 " +
                "L 2.1,8.4 L 3.2,7.1 L 2.9,5.4 L 4.3,4.8 L 5,3.2 L 6.7,3.6 Z"),
        });

    /// <summary>A tick, for the selected item in a menu.</summary>
    public static Control Check() => Draw(
        12,
        new Polyline { Points = { new Point(3.5, 8), new Point(6.5, 11), new Point(12.5, 4.5) } });

    /// <summary>A question mark in a circle, for help.</summary>
    public static Control Help() => Draw(
        Outline2(),
        new ShapePath
        {
            Data = Geometry.Parse("M 6.2,6.3 A 1.9,1.9 0 1 1 8,9.3 L 8,10.4"),
        },
        Dot(12.3));

    /// <summary>Three dots, for a menu.</summary>
    public static Control More() => Draw(
        Dot(4.5), Dot(8), Dot(11.5));

    // The four caption glyphs, built through Draw like every other icon here so the chrome has one
    // way of drawing an icon rather than two. They were briefly hand-rolled as bare shapes in a
    // fixed-size Panel next to the title bar, on the belief that they were failing to render; the
    // real fault was in the screenshot harness (ADR 0016), and either construction works.

    /// <summary>The caption's minimise bar.</summary>
    public static Control CaptionMinimise() => Draw(
        CaptionSize,
        new Line { StartPoint = new Point(3, 8), EndPoint = new Point(13, 8) });

    /// <summary>The caption's maximise square.</summary>
    public static Control CaptionMaximise() => Draw(
        CaptionSize,
        CaptionSquare(new Rect(3, 3, 10, 10)));

    /// <summary>The caption's restore-down pair: the front window, and the one behind it.</summary>
    public static Control CaptionRestore() => Draw(
        CaptionSize,
        CaptionSquare(new Rect(3, 5.5, 7.5, 7.5)),
        new ShapePath { Data = Geometry.Parse("M 5.5,5 L 5.5,3 L 13,3 L 13,10.5 L 11,10.5") });

    /// <summary>The caption's close cross. Wider than <see cref="Close"/>, which is a tab's.</summary>
    public static Control CaptionClose() => Draw(
        CaptionSize,
        new Line { StartPoint = new Point(3.5, 3.5), EndPoint = new Point(12.5, 12.5) },
        new Line { StartPoint = new Point(3.5, 12.5), EndPoint = new Point(12.5, 3.5) });

    /// <summary>Caption glyphs are 10px, the system metric, against the toolbar's 16.</summary>
    private const double CaptionSize = 10;

    private static Shape CaptionSquare(Rect rect) => new Rectangle
    {
        Width = rect.Width,
        Height = rect.Height,
        RadiusX = 0.5,
        RadiusY = 0.5,
        Margin = new Thickness(rect.X, rect.Y, 0, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
    };

    /// <summary>A circle on the same 16x16 grid as <see cref="Outline"/>'s square.</summary>
    private static Shape Outline2() => new Ellipse
    {
        Width = 12,
        Height = 12,
        Margin = new Thickness(2, 2, 0, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
    };

    private static Shape Outline() => new Rectangle
    {
        Width = 11,
        Height = 11,
        RadiusX = 1.5,
        RadiusY = 1.5,
        Margin = new Thickness(2.5, 2.5, 0, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
    };

    private static Shape Fill(Rect rect) => new Rectangle
    {
        Width = rect.Width,
        Height = rect.Height,
        RadiusX = 0.75,
        RadiusY = 0.75,
        Margin = new Thickness(rect.X, rect.Y, 0, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        Tag = FilledTag,
    };

    private static Shape Dot(double y) => new Ellipse
    {
        Width = 1.7,
        Height = 1.7,
        Margin = new Thickness(7.2, y - 0.85, 0, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        Tag = FilledTag,
    };

    private const string FilledTag = "filled";

    private static Control Draw(params Shape[] shapes) => Draw(Size, shapes);

    private static Control Draw(double size, params Shape[] shapes)
    {
        var canvas = new Panel { Width = Size, Height = Size };
        foreach (var shape in shapes)
        {
            // A filled shape takes the foreground as its fill; everything else is a stroke. Both
            // bind rather than copy, so the icon follows hover and theme changes with its label.
            if (ReferenceEquals(shape.Tag, FilledTag))
            {
                shape.Bind(Shape.FillProperty, InheritedForeground());
            }
            else
            {
                shape.Bind(Shape.StrokeProperty, InheritedForeground());
                shape.StrokeThickness = Stroke;
                shape.StrokeLineCap = PenLineCap.Round;
                shape.StrokeJoin = PenLineJoin.Round;
            }
            canvas.Children.Add(shape);
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// The owning button's <c>Foreground</c>, resolved at attach time. Bound rather than copied so
    /// the icon tracks hover, disabled state and a system theme change exactly as its label does.
    /// </summary>
    private static Avalonia.Data.Binding InheritedForeground() => new Avalonia.Data.Binding
    {
        Path = nameof(Button.Foreground),
        RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor)
        {
            AncestorType = typeof(Button),
        },
    };
}
