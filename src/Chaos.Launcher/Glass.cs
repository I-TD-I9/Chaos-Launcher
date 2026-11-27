using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Chaos.Launcher;

/// <summary>A frosted glass panel.</summary>
public class Glass : Border
{
    public static readonly DependencyProperty SlabProperty = DependencyProperty.RegisterAttached(
        "Slab", typeof(ImageSource), typeof(Glass),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static ImageSource? GetSlab(DependencyObject element) => (ImageSource?)element.GetValue(SlabProperty);

    public static void SetSlab(DependencyObject element, ImageSource? value) => element.SetValue(SlabProperty, value);

    // Last drawn position in the window.
    Point drawnAt = new(double.NaN, double.NaN);

    public Glass()
    {
        LayoutUpdated += (_, _) =>
        {
            if (GetSlab(this) is not null && InWindow() is { } at && at != drawnAt)
                InvalidateVisual();
        };
    }

    Point? InWindow()
    {
        if (Window.GetWindow(this)?.Content is not Visual root || !root.IsAncestorOf(this))
            return null;
        return TransformToAncestor(root).Transform(new Point(0, 0));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (GetSlab(this) is { } slab && InWindow() is { } at)
        {
            drawnAt = at;
            var corners = CornerRadius.TopLeft;
            drawing.PushClip(new RectangleGeometry(new Rect(RenderSize), corners, corners));
            drawing.DrawImage(slab, new Rect(-at.X, -at.Y, slab.Width, slab.Height));
            drawing.Pop();
        }
        base.OnRender(drawing);
    }
}
