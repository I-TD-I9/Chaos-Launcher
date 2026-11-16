using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Chaos.Launcher;

/// <summary>
/// The launcher's one progress bar, styled after the game's Life Points: it counts down what is
/// left to 0, and its gauge drains with it, like an opponent losing Life Points. It shows the
/// card pictures still to fetch, and the kilobytes of a launcher update still to come.
/// </summary>
public partial class LpMeter : UserControl
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(
        nameof(Left), typeof(long), typeof(LpMeter), new PropertyMetadata(0L, Changed));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long), typeof(LpMeter), new PropertyMetadata(0L, Changed));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LpMeter), new PropertyMetadata(null, Changed));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(LpMeter), new PropertyMetadata(null, Changed));

    public LpMeter()
    {
        InitializeComponent();
        Gauge.SizeChanged += (_, _) => Show();
        Show();
    }

    /// <summary>What is left to do, which the digits show.</summary>
    public long Left
    {
        get => (long)GetValue(LeftProperty);
        set => SetValue(LeftProperty, value);
    }

    /// <summary>What there was to do at the start: the full gauge.</summary>
    public long Total
    {
        get => (long)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    /// <summary>The status beside the digits, on the right.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>What the digits count, as "KB", or nothing.</summary>
    public string? Unit
    {
        get => (string?)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    static void Changed(DependencyObject meter, DependencyPropertyChangedEventArgs e) => ((LpMeter)meter).Show();

    // The digits are set straight from each report, with no rolling or smoothing, so they never
    // lag the true count.
    void Show()
    {
        long left = Math.Max(0, Left), total = Math.Max(0, Total);
        Digits.Text = left.ToString(CultureInfo.InvariantCulture);
        StatusText.Text = Text ?? "";
        UnitText.Text = Unit ?? "";
        UnitText.Visibility = string.IsNullOrEmpty(Unit) ? Visibility.Collapsed : Visibility.Visible;
        double share = total == 0 ? 0 : Math.Min(1, (double)left / total);
        Level.Width = Gauge.ActualWidth * share;
    }
}
