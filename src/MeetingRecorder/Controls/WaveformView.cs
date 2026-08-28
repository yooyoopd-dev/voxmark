using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MeetingRecorder.Models;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Controls;

/// <summary>One speaker-change boundary drawn over the waveform.</summary>
public readonly record struct WaveformBoundary(double Seconds, Color Color, string Initial, bool IsOpen);

/// <summary>
/// The live waveform, design guide section 04: a rolling window of the last
/// 45 seconds with the playhead pinned to the right edge.
///
/// It draws a real min/max envelope from ~10 ms slices rather than one bar
/// per capture buffer, with the RMS core filled in brighter inside it. That
/// detail is the point: the operator repairs marks by eye against this, so a
/// pause between words has to be visible as a gap, not averaged away.
///
/// Section 01 borrows Ultraschall's chapter-marker shape for speaker changes:
/// a 1px line at every mark boundary with the speaker's initial in a small
/// flag, drawn on top so turn-taking reads at a glance. The boundary of the
/// mark that is still open is drawn heavier, because that is the one the
/// arrow keys move.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private readonly List<WaveSlice> _slices = new();
    private IReadOnlyList<WaveformBoundary> _boundaries = Array.Empty<WaveformBoundary>();

    private static readonly Brush PeakBrush = Frozen(Color.FromRgb(0x4C, 0x51, 0x63));
    private static readonly Brush PeakRecentBrush = Frozen(Color.FromRgb(0x63, 0x68, 0x7D));
    private static readonly Brush RmsBrush = Frozen(Palette.TextFaint);
    private static readonly Brush RmsRecentBrush = Frozen(Palette.TextDim);
    private static readonly Pen CentreLine = FrozenPen(Color.FromArgb(0x14, 0xE9, 0xE9, 0xED), 1);
    private static readonly Pen GridLine = FrozenPen(Color.FromArgb(0x0C, 0xE9, 0xE9, 0xED), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromArgb(0x90, 0xE9, 0xE9, 0xED), 2);

    public WaveformView()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Length of the rolling window. 45 s, per the section 04 mockup.</summary>
    public double WindowSeconds { get; set; } = 45;

    /// <summary>Current file position; the right edge of the window.</summary>
    public double CurrentSeconds { get; set; }

    /// <summary>Dims the trace while paused, so a flat line is never mistaken for silence.</summary>
    public bool IsPaused { get; set; }

    public void Push(WaveSlice slice)
    {
        _slices.Add(slice);

        // Keep a little more than one window's worth so a resize never
        // reveals a gap at the left edge.
        var cutoff = slice.Seconds - WindowSeconds * 1.2;
        if (_slices.Count > 512 && _slices[0].Seconds < cutoff)
        {
            var keepFrom = _slices.FindIndex(s => s.Seconds >= cutoff);
            if (keepFrom > 0) _slices.RemoveRange(0, keepFrom);
        }
    }

    public void Push(IReadOnlyList<WaveSlice> slices)
    {
        foreach (var slice in slices) Push(slice);
    }

    public void SetBoundaries(IReadOnlyList<WaveformBoundary> boundaries) => _boundaries = boundaries;

    public void Clear() => _slices.Clear();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        const double padX = 10;
        var innerWidth = Math.Max(1, width - padX * 2);
        var centreY = height / 2.0;
        var half = Math.Max(4, height / 2.0 - 14);

        var windowEnd = Math.Max(WindowSeconds, CurrentSeconds);
        var windowStart = windowEnd - WindowSeconds;

        DrawSecondGrid(dc, padX, innerWidth, height, windowStart);
        dc.DrawLine(CentreLine, new Point(0, centreY), new Point(width, centreY));

        // One column every ~3px: fine enough to show syllables, coarse enough
        // that a 45 s window still aggregates rather than aliases.
        var columns = Math.Max(24, (int)(innerWidth / 3.0));
        var columnWidth = innerWidth / columns;
        var barWidth = Math.Max(1.0, columnWidth - 1.0);
        var opacity = IsPaused ? 0.3 : 1.0;

        var mins = new float[columns];
        var maxs = new float[columns];
        var rms = new float[columns];
        var seen = new bool[columns];

        foreach (var slice in _slices)
        {
            if (slice.Seconds < windowStart || slice.Seconds > windowEnd) continue;
            var index = (int)((slice.Seconds - windowStart) / WindowSeconds * columns);
            index = Math.Clamp(index, 0, columns - 1);

            if (!seen[index])
            {
                seen[index] = true;
                mins[index] = slice.Min;
                maxs[index] = slice.Max;
                rms[index] = slice.Rms;
                continue;
            }

            if (slice.Min < mins[index]) mins[index] = slice.Min;
            if (slice.Max > maxs[index]) maxs[index] = slice.Max;
            if (slice.Rms > rms[index]) rms[index] = slice.Rms;
        }

        dc.PushOpacity(opacity);
        for (var i = 0; i < columns; i++)
        {
            var x = padX + i * columnWidth + (columnWidth - barWidth) / 2.0;
            var recent = i >= columns - 6;

            if (!seen[i])
            {
                // No audio for this column yet — a hairline keeps the
                // baseline continuous instead of leaving a hole.
                dc.DrawRectangle(recent ? PeakRecentBrush : PeakBrush, null,
                    new Rect(x, centreY - 0.5, barWidth, 1));
                continue;
            }

            var top = centreY - maxs[i] * half;
            var bottom = centreY - mins[i] * half;
            if (bottom - top < 1) { top = centreY - 0.5; bottom = centreY + 0.5; }
            dc.DrawRectangle(recent ? PeakRecentBrush : PeakBrush, null,
                new Rect(x, top, barWidth, bottom - top));

            var core = rms[i] * half;
            if (core > 0.6)
            {
                dc.DrawRectangle(recent ? RmsRecentBrush : RmsBrush, null,
                    new Rect(x, centreY - core, barWidth, core * 2));
            }
        }
        dc.Pop();

        DrawBoundaries(dc, padX, innerWidth, height, windowStart, windowEnd);

        // Playhead: the right edge is always "now".
        var playheadX = width - padX;
        dc.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, height));

        DrawLabel(dc);
    }

    /// <summary>A faint tick every 5 s, so a nudge of a few tenths has a scale to read against.</summary>
    private void DrawSecondGrid(DrawingContext dc, double padX, double innerWidth, double height,
                                double windowStart)
    {
        var first = Math.Ceiling(windowStart / 5.0) * 5.0;
        for (var t = first; t < windowStart + WindowSeconds; t += 5.0)
        {
            var x = padX + (t - windowStart) / WindowSeconds * innerWidth;
            dc.DrawLine(GridLine, new Point(x, 0), new Point(x, height));
        }
    }

    private void DrawBoundaries(DrawingContext dc, double padX, double innerWidth, double height,
                                double windowStart, double windowEnd)
    {
        foreach (var boundary in _boundaries)
        {
            if (boundary.Seconds < windowStart || boundary.Seconds > windowEnd) continue;

            var x = padX + (boundary.Seconds - windowStart) / WindowSeconds * innerWidth;
            var brush = Frozen(boundary.Color);
            var pen = new Pen(brush, boundary.IsOpen ? 2 : 1);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, 0), new Point(x, height));

            var text = Text(boundary.Initial, 9, Palette.Void);
            var flag = new Rect(x, 3, text.Width + 8, text.Height + 2);
            dc.DrawRoundedRectangle(brush, null, flag, 2, 2);
            dc.DrawText(text, new Point(x + 4, 4));

            if (!boundary.IsOpen) continue;

            // The open mark's start is what ← and → move; label it with its
            // own time so the nudge is readable without leaving the waveform.
            var stamp = Text(Tenths(boundary.Seconds), 9.5, boundary.Color);
            var box = new Rect(x + 2, height - stamp.Height - 5, stamp.Width + 8, stamp.Height + 3);
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xCC, 0x16, 0x18, 0x26)), pen, box, 3, 3);
            dc.DrawText(stamp, new Point(x + 6, height - stamp.Height - 3.5));
        }
    }

    private void DrawLabel(DrawingContext dc)
    {
        var label = Text("LIVE · LAST " + ((int)WindowSeconds).ToString() + " S", 10, Palette.TextFaint);
        dc.DrawText(label, new Point(14, 8));
    }

    private static string Tenths(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" +
               span.Seconds.ToString("00") + "." + (span.Milliseconds / 100).ToString("0");
    }

    private FormattedText Text(string value, double size, Color color)
    {
        return new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            Frozen(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
