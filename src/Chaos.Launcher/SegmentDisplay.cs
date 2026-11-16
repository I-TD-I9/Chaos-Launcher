using System.Windows;
using System.Windows.Media;

namespace Chaos.Launcher;

/// <summary>
/// Digits drawn as a seven-segment counter, slanted like the game's Life Points: only the lit
/// segments, so nothing else reads as noise on the glass, and a "1" as narrow as its one bar
/// so that 1126 reads as one number.
/// </summary>
public sealed class SegmentDisplay : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SegmentDisplay),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DigitHeightProperty = DependencyProperty.Register(
        nameof(DigitHeight), typeof(double), typeof(SegmentDisplay),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SegmentDisplay),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The digits to show; anything that is not a digit is a blank of a digit's width.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double DigitHeight
    {
        get => (double)GetValue(DigitHeightProperty);
        set => SetValue(DigitHeightProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    // The segments each digit lights: a top, b top right, c bottom right, d bottom, e bottom
    // left, f top left, g middle.
    static readonly string[] Lit = ["abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcdfg"];

    // The cell of a digit is 0.54 of its height wide, the segments 0.13 of it thick, and the
    // digits 0.2 of it apart.
    double Wide => DigitHeight * 0.54;
    double Thick => Math.Max(1.6, DigitHeight * 0.13);
    double Gap => DigitHeight * 0.2;

    double WidthOf(string text)
    {
        double x = 0;
        foreach (char c in text)
            x += (c == '1' ? Thick : Wide) + Gap;
        // The slant leans the tops right by a little under an eighth of the height.
        return text.Length == 0 ? 0 : Math.Ceiling(x - Gap + DigitHeight * 0.2);
    }

    protected override Size MeasureOverride(Size available) => new(WidthOf(Text ?? ""), DigitHeight);

    protected override void OnRender(DrawingContext drawing)
    {
        string text = Text ?? "";
        double h = DigitHeight, w = Wide, t = Thick, g = t * 0.22, half = t / 2;
        double left = half, right = w - half, top = half, middle = h / 2, bottom = h - half;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            void Polygon(params Point[] points)
            {
                context.BeginFigure(points[0], true, true);
                context.PolyLineTo(points[1..], false, true);
            }
            void Across(double dx, double x0, double x1, double y) => Polygon(
                new(dx + x0, y), new(dx + x0 + half, y - half), new(dx + x1 - half, y - half),
                new(dx + x1, y), new(dx + x1 - half, y + half), new(dx + x0 + half, y + half));
            void Down(double dx, double x, double y0, double y1) => Polygon(
                new(dx + x, y0), new(dx + x + half, y0 + half), new(dx + x + half, y1 - half),
                new(dx + x, y1), new(dx + x - half, y1 - half), new(dx + x - half, y0 + half));

            double at = 0;
            foreach (char c in text)
            {
                bool one = c == '1';
                // A "1" is drawn against its own left edge, not the right edge of a full cell.
                double dx = at - (one ? w - t : 0);
                if (c is >= '0' and <= '9')
                    foreach (char segment in Lit[c - '0'])
                        switch (segment)
                        {
                            case 'a': Across(dx, left + g, right - g, top); break;
                            case 'g': Across(dx, left + g, right - g, middle); break;
                            case 'd': Across(dx, left + g, right - g, bottom); break;
                            case 'f': Down(dx, left, top + g, middle - g); break;
                            case 'b': Down(dx, right, top + g, middle - g); break;
                            case 'e': Down(dx, left, middle + g, bottom - g); break;
                            case 'c': Down(dx, right, middle + g, bottom - g); break;
                        }
                at += (one ? t : w) + Gap;
            }
        }
        // Moved right a little, then slanted 7 degrees as the game's counter is.
        var slant = new TransformGroup();
        slant.Children.Add(new TranslateTransform(h * 0.12, 0));
        slant.Children.Add(new SkewTransform(-7, 0));
        geometry.Transform = slant;
        geometry.Freeze();
        drawing.DrawGeometry(Fill, null, geometry);
    }
}
