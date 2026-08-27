using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Controls;

/// <summary>One speaker-change boundary drawn over the waveform.</summary>
public readonly record struct WaveformBoundary(double Seconds, Color Color, string Initial);

/// <summary>
/// The live waveform, design guide section 04: a rolling window of the last
/// 45 seconds, bars centred on a hairline, the playhead pinned to the right
/// edge.
///
/// Section 01 borrows Ultraschall's chapter-marker shape for speaker
/// changes: a 1px line at every mark boundary with the speaker's initial in
/// a small flag, drawn on top of the waveform so the operator can read
/// turn-taking at a glance.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private readonly List<(double Seconds, double Level)> _samples = new();
    private IReadOnlyList<WaveformBoundary> _boundaries = Array.Empty<WaveformBoundary>();

    private static readonly Brush BarBrush = Palette.TextFaintBrush;
    private static readonly Brush RecentBarBrush = Palette.TextDimBrush;
    private static readonly Pen CentreLine = FrozenPen(Color.FromArgb(0x12, 0xE9, 0xE9, 0xED), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromArgb(0x80, 0xE9, 0xE9, 0xED), 2);

    public WaveformView()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Length of the rolling window. 45 s, per the section 04 mockup.</summary>
    public double WindowSeconds { get; set; } = 45;

    public int BarCount { get; set; } = 120;

    /// <summary>Current file position; the right edge of the window.</summary>
    public double CurrentSeconds { get; set; }

    /// <summary>Dims the trace while paused, so a flat line is never mistaken for silence.</summary>
    public bool IsPaused { get; set; }

    public void Push(double seconds, double level)
    {
        _samples.Add((seconds, Math.Clamp(level, 0, 1)));

        // Keep a little more than one window's worth so a resize never
        // reveals a gap at the left edge.
        var cutoff = seconds - WindowSeconds * 1.2;
        if (_samples.Count > 64 && _samples[0].Seconds < cutoff)
        {
            var keepFrom = _samples.FindIndex(s => s.Seconds >= cutoff);
            if (keepFrom > 0) _samples.RemoveRange(0, keepFrom);
        }
    }

    public void SetBoundaries(IReadOnlyList<WaveformBoundary> boundaries) => _boundaries = boundaries;

    public void Clear() => _samples.Clear();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        // A transparent background is still needed for the element to have
        // a rendered area at all.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        const double padX = 10;
        var innerWidth = Math.Max(1, width - padX * 2);
        var centreY = height / 2.0;
        var maxBar = Math.Max(6, height / 2.0 - 12);

        dc.DrawLine(CentreLine, new Point(0, centreY), new Point(width, centreY));

        var windowEnd = Math.Max(WindowSeconds, CurrentSeconds);
        var windowStart = windowEnd - WindowSeconds;

        var bars = Math.Max(12, BarCount);
        var slotWidth = innerWidth / bars;
        var barWidth = Math.Max(1.5, slotWidth - 2);
        var opacity = IsPaused ? 0.35 : 1.0;

        // Bucket the samples into fixed columns so the trace scrolls
        // smoothly instead of jittering when buffers arrive unevenly.
        var peaks = new double[bars];
        foreach (var (seconds, level) in _samples)
        {
            if (seconds < windowStart || seconds > windowEnd) continue;
            var index = (int)((seconds - windowStart) / WindowSeconds * bars);
            index = Math.Clamp(index, 0, bars - 1);
            if (level > peaks[index]) peaks[index] = level;
        }

        for (var i = 0; i < bars; i++)
        {
            var level = peaks[i];
            var barHeight = Math.Max(2, level * maxBar * 2);
            var x = padX + i * slotWidth + (slotWidth - barWidth) / 2.0;
            var rect = new Rect(x, centreY - barHeight / 2.0, barWidth, barHeight);
            var brush = i >= bars - 8 ? RecentBarBrush : BarBrush;
            dc.PushOpacity(opacity);
            dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
            dc.Pop();
        }

        DrawBoundaries(dc, padX, innerWidth, height, windowStart, windowEnd);

        // Playhead: the right edge is always "now".
        var playheadX = width - padX;
        dc.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, height));

        DrawLabel(dc);
    }

    private void DrawBoundaries(DrawingContext dc, double padX, double innerWidth, double height,
                                double windowStart, double windowEnd)
    {
        foreach (var boundary in _boundaries)
        {
            if (boundary.Seconds < windowStart || boundary.Seconds > windowEnd) continue;

            var x = padX + (boundary.Seconds - windowStart) / WindowSeconds * innerWidth;
            var brush = new SolidColorBrush(boundary.Color);
            brush.Freeze();
            var pen = new Pen(brush, 1);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, 0), new Point(x, height));

            var text = Text(boundary.Initial, 9, Palette.Void);
            var flag = new Rect(x, 3, text.Width + 8, text.Height + 2);
            dc.DrawRoundedRectangle(brush, null, flag, 2, 2);
            dc.DrawText(text, new Point(x + 4, 4));
        }
    }

    private void DrawLabel(DrawingContext dc)
    {
        var label = Text("LIVE · LAST " + ((int)WindowSeconds).ToString() + " S", 10, Palette.TextFaint);
        dc.DrawText(label, new Point(14, 8));
    }

    private FormattedText Text(string value, double size, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
