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
///
/// The trace is auto-gained and shaped rather than drawn at raw amplitude.
/// A meeting mic in a room sits far below full scale — a conference speaker
/// three metres away peaks around −30 dBFS — so a linear trace uses a tenth
/// of the lane whatever height the lane is given, and the pause an operator
/// is trying to see is a two-pixel dip. See <see cref="UpdateGain"/> and
/// <see cref="Shape"/>: together they fill the lane with the loudest recent
/// speech and push the quiet parts further down than a linear scale would,
/// which is what turns "louder" into a visible difference rather than an
/// arithmetic one.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    /// <summary>Where the loudest recent peak is drawn, as a fraction of the half-height.</summary>
    private const double TargetPeak = 0.92;

    /// <summary>
    /// Bounds on the auto-gain. Never below 1 — a hot input is shown as it
    /// is, never shrunk — and never above 20, so a silent room's noise floor
    /// cannot be amplified into a waveform that looks like speech.
    /// </summary>
    private const double MinGain = 1.0;
    private const double MaxGain = 20.0;

    /// <summary>
    /// How much of a shaped value survives at the bottom of the scale. Below
    /// 1 the curve expands contrast: at 0.4, a sound a tenth as loud as the
    /// peak draws about a twentieth of the height rather than a tenth, so
    /// room noise stays a flat band and speech stands off it.
    /// </summary>
    private const double ContrastFloor = 0.4;

    private readonly List<WaveSlice> _slices = new();
    private IReadOnlyList<WaveformBoundary> _boundaries = Array.Empty<WaveformBoundary>();

    private double _gain = 1.0;

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

    public void Clear()
    {
        _slices.Clear();
        _gain = 1.0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        const double padX = 10;
        var innerWidth = Math.Max(1, width - padX * 2);
        var centreY = height / 2.0;
        // 9px of headroom each side, which is what the marker flags at the top
        // and the open mark's timecode at the bottom actually need. The rest
        // of the lane belongs to the trace: this is a taller well than it was,
        // and the point of the extra height is the waveform, not the padding.
        var half = Math.Max(4, height / 2.0 - 9);

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

        // The gain follows the window that is actually on screen, so it settles
        // on this room and this microphone rather than on a level set once at
        // setup — and it is recomputed before anything is drawn, so every
        // column in this frame is drawn at one scale.
        var windowPeak = 0.0;
        for (var i = 0; i < columns; i++)
        {
            if (!seen[i]) continue;
            windowPeak = Math.Max(windowPeak, Math.Max(Math.Abs(mins[i]), Math.Abs(maxs[i])));
        }
        UpdateGain(windowPeak);

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

            var top = centreY - Signed(maxs[i]) * half;
            var bottom = centreY - Signed(mins[i]) * half;
            if (bottom - top < 1) { top = centreY - 0.5; bottom = centreY + 0.5; }
            dc.DrawRectangle(recent ? PeakRecentBrush : PeakBrush, null,
                new Rect(x, top, barWidth, bottom - top));

            var core = Shape(rms[i]) * half;
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
        // The gain is named rather than applied silently: a trace that fills
        // the lane at ×14 is not the same evidence as one that fills it at ×1,
        // and an operator judging a pause deserves to know which they have.
        var label = Text("LIVE · LAST " + ((int)WindowSeconds).ToString() + " S · ×" +
                         _gain.ToString(_gain < 10 ? "0.0" : "0"), 10, Palette.TextFaint);
        dc.DrawText(label, new Point(14, 8));
    }

    /// <summary>
    /// Track the loudest thing in the visible window towards
    /// <see cref="TargetPeak"/> of the lane.
    ///
    /// Asymmetric on purpose. Turning the gain down happens quickly, so a
    /// sudden loud passage is scaled before it can spend a second clipped
    /// against the edges; turning it up happens slowly, so the trace does not
    /// pump between words. The 0.02 floor is what stops a silent room from
    /// running the gain to its ceiling and drawing the noise floor as speech.
    /// </summary>
    private void UpdateGain(double windowPeak)
    {
        var target = Math.Clamp(TargetPeak / Math.Max(windowPeak, 0.02), MinGain, MaxGain);
        _gain += (target - _gain) * (target < _gain ? 0.5 : 0.06);
    }

    /// <summary>
    /// One amplitude, gained and contrast-shaped, as a 0..1 fraction of the
    /// half-height.
    ///
    /// The curve is <c>v · (floor + (1 − floor)·v)</c>: it leaves the loudest
    /// peak exactly at the top of the lane and bends everything below it
    /// down, so a quiet passage reads as quiet even after the gain has made
    /// the loud one big. Clamped at 1 — over-scale is drawn flat against the
    /// edge rather than outside the well.
    /// </summary>
    private double Shape(double magnitude)
    {
        if (magnitude <= 0) return 0;
        var gained = Math.Min(1.0, magnitude * _gain);
        return gained * (ContrastFloor + (1 - ContrastFloor) * gained);
    }

    /// <summary>Shape a value that carries a sign, keeping the envelope asymmetric.</summary>
    private double Signed(double value) => value < 0 ? -Shape(-value) : Shape(value);

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
