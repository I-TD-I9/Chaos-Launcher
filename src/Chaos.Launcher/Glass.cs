using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Chaos.Launcher;

/// <summary>
/// A panel of frosted glass: a Border that first lays down the part of the blurred slab that lies
/// under it, then its own tint and rim as any Border does.
/// </summary>
/// <remarks>
/// The blurred slab is one picture the size of the window, made once (MainWindow.Blurred) and
/// handed down the tree through the inherited <see cref="SlabProperty"/>. The window never moves
/// or resizes its content, so each panel only has to find where it sits in the window and draw
/// that part of the picture, which costs no more than an image; there is no live blur.
/// </remarks>
public class Glass : Border
{
    /// <summary>The blurred slab, the size of the window, or null when there is no slab.</summary>
    public static readonly DependencyProperty SlabProperty = DependencyProperty.RegisterAttached(
        "Slab", typeof(ImageSource), typeof(Glass),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static ImageSource? GetSlab(DependencyObject element) => (ImageSource?)element.GetValue(SlabProperty);

    public static void SetSlab(DependencyObject element, ImageSource? value) => element.SetValue(SlabProperty, value);

    // Where in the window the panel was last drawn: a page coming up can move it without
    // changing its size, which by itself would not draw it again.
    Point drawnAt = new(double.NaN, double.NaN);

    public Glass()
    {
        LayoutUpdated += (_, _) =>
        {
            if (GetSlab(this) is not null && InWindow() is { } at && at != drawnAt)
                InvalidateVisual();
        };
    }

    // The panel's top left corner in the window's content, or null when it isn't in a window.
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
