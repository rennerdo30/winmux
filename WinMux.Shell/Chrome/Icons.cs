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

    /// <summary>Three dots, for a menu.</summary>
    public static Control More() => Draw(
        Dot(4.5), Dot(8), Dot(11.5));

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
