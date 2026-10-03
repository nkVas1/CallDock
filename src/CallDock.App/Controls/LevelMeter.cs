using System.Windows;
using System.Windows.Media;

namespace CallDock.App.Controls;

/// <summary>
/// A level meter like on a mixing desk: −60…0 dBFS mapped to 0…1, green up to −18 dB, amber up to −6 dB,
/// red above; a thin mark holds the recent peak. Drawn directly (no templates): it redraws ten times a second.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(nameof(Peak), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private const double Amber = 42.0 / 60; // −18 dBFS
    private const double Red = 54.0 / 60;   // −6 dBFS
    private const int Segments = 30;
    private static readonly Brush Track = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, 8);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var track = Track;
        var low = Brush("MeterLowBrush", Colors.MediumSeaGreen);
        var mid = Brush("MeterMidBrush", Colors.Goldenrod);
        var high = Brush("MeterHighBrush", Colors.IndianRed);
        Brush Zone(double position) => position >= Red ? high : position >= Amber ? mid : low;
        var gap = 2.0;
        var segment = (width - gap * (Segments - 1)) / Segments;
        if (segment < 1) { segment = width / Segments; gap = 0; }
        var level = Math.Clamp(Level, 0, 1);
        for (var i = 0; i < Segments; i++)
        {
            var position = (i + 0.5) / Segments;
            var lit = IsActive && position <= level;
            var brush = lit ? Zone(position) : track;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (segment + gap), 0, segment, height), 1.5, 1.5);
        }
        var peak = Math.Clamp(Peak, 0, 1);
        if (IsActive && peak > 0.02)
        {
            var x = Math.Min(width - 2, peak * width);
            dc.DrawRectangle(Zone(peak), null, new Rect(x, -2, 2, height + 4));
        }
    }

    private Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
