using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Chaos.Launcher;

public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    IEnumerable<FormattedText> GetLetters()
    {
        var face = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (char c in Text ?? "")
            yield return new FormattedText(c.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                           face, FontSize, Foreground, dip);
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        foreach (var letter in GetLetters())
        {
            width += letter.WidthIncludingTrailingWhitespace + Tracking * FontSize;
            height = Math.Max(height, letter.Height);
        }
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double x = Tracking * FontSize / 2;
        foreach (var letter in GetLetters())
        {
            drawing.DrawText(letter, new Point(x, 0));
            x += letter.WidthIncludingTrailingWhitespace + Tracking * FontSize;
        }
    }
}
