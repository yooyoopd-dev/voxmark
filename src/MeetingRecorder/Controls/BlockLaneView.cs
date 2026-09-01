using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeetingRecorder.Models;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Controls;

/// <summary>
/// The coloured block lane — the app's primary object, taken from
/// Ultraschall literally (design guide section 01): "a mark is a coloured
/// block with a start and an end, owned by one speaker, and it is the only
/// data the app produces besides audio."
///
/// The same element is the whole-session minimap under the live waveform
/// (section 04), the lane above the expanded marks dock (section 06), and
/// the little session strip in the library (section 07).
/// </summary>
public sealed class BlockLaneView : FrameworkElement
{
    private static readonly Pen ViewportPen = FrozenPen(Palette.Text, 1.5);
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0xF7, 0xD5, 0xDC), 1.5);
    private static readonly Pen PlayheadPen = FrozenPen(Palette.Text, 1.5);
    private static readonly Brush ViewportFill = FrozenBrush(Color.FromArgb(0x10, 0xE9, 0xE9, 0xED));

    private IReadOnlyList<Mark> _marks = Array.Empty<Mark>();
    private IReadOnlyList<Mark> _live = Array.Empty<Mark>();

    public double TotalSeconds { get; set; } = 1;

    /// <summary>Highlighted span, used by the minimap to show the live 45 s window.</summary>
    public double? ViewportStart { get; set; }
    public double? ViewportEnd { get; set; }

    public long? SelectedMarkId { get; set; }

    /// <summary>Review position set by clicking the lane; "split at playhead" uses it.</summary>
    public double? PlayheadSeconds { get; set; }

    /// <summary>Draw a bright edge at the right, i.e. "now" while recording.</summary>
    public bool ShowLiveEdge { get; set; }

    /// <summary>Two rows, so overlapping marks stay separately readable (section 09).</summary>
    public bool AllowTwoRows { get; set; }

    public double BlockInset { get; set; } = 5;

    /// <summary>Fires with the clicked position in seconds.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>Fires when the click landed on a mark.</summary>
    public event Action<long>? MarkClicked;

    public BlockLaneView()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
    }

    public void SetMarks(IReadOnlyList<Mark> marks)
    {
        _marks = marks;
        InvalidateVisual();
    }

    /// <summary>
    /// The turns that are open right now, drawn as blocks that grow with the
    /// recording.
    ///
    /// Separate from <see cref="SetMarks"/> because they are not marks yet —
    /// they have no id, no end, and no journal entry — and because they are
    /// drawn differently: full-strength colour and a bright leading edge,
    /// where a closed mark is tinted back. Without them the minimap shows
    /// every speaker except the one talking, which is the one the operator is
    /// looking for.
    /// </summary>
    public void SetLive(IReadOnlyList<Mark> live)
    {
        _live = live;
        InvalidateVisual();
    }

    /// <summary>Turn on click-to-scrub. Off for the read-only library strip.</summary>
    public bool IsInteractive
    {
        get => IsHitTestVisible;
        set
        {
            IsHitTestVisible = value;
            Cursor = value ? Cursors.Hand : Cursors.Arrow;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0 || TotalSeconds <= 0) return;

        var position = e.GetPosition(this);
        var seconds = Math.Clamp(position.X / ActualWidth * TotalSeconds, 0, TotalSeconds);
        Scrubbed?.Invoke(seconds);

        var hit = _marks.FirstOrDefault(m => seconds >= m.StartSeconds && seconds <= m.EndSeconds);
        if (hit is not null) MarkClicked?.Invoke(hit.Id);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var total = Math.Max(1e-6, TotalSeconds);
        var rows = AssignRows();
        var rowCount = AllowTwoRows && rows.Values.Any(r => r > 0) ? 2 : 1;
        var laneHeight = (height - BlockInset * 2) / rowCount;

        foreach (var mark in _marks)
        {
            var left = mark.StartSeconds / total * width;
            var right = mark.EndSeconds / total * width;
            var blockWidth = Math.Max(1.5, right - left);
            var row = rows.TryGetValue(mark.Id, out var r) ? Math.Min(r, rowCount - 1) : 0;
            var top = BlockInset + row * laneHeight;
            var rect = new Rect(left, top, blockWidth, Math.Max(2, laneHeight - (rowCount > 1 ? 2 : 0)));

            var brush = FrozenBrush(Palette.Tint(Palette.ForSlot(mark.SpeakerSlot), 0.85));
            dc.DrawRoundedRectangle(brush, null, rect, 3, 3);

            if (SelectedMarkId == mark.Id)
            {
                dc.DrawRoundedRectangle(null, SelectionPen, Inflate(rect, 1), 3, 3);
            }
        }

        foreach (var mark in _live)
        {
            var left = mark.StartSeconds / total * width;
            var right = mark.EndSeconds / total * width;
            var blockWidth = Math.Max(2, right - left);
            var rect = new Rect(left, BlockInset, blockWidth, Math.Max(2, laneHeight - (rowCount > 1 ? 2 : 0)));

            // Undimmed, unlike a closed mark, and capped with a bright edge at
            // the growing end: this block is still being written.
            var colour = Palette.ForSlot(mark.SpeakerSlot);
            dc.DrawRoundedRectangle(FrozenBrush(colour), null, rect, 3, 3);
            dc.DrawRectangle(Palette.TextBrush, null,
                new Rect(rect.Right - 1.5, rect.Y, 1.5, rect.Height));
        }

        if (ViewportStart is { } start && ViewportEnd is { } end && end > start)
        {
            var left = Math.Max(0, start / total * width);
            var right = Math.Min(width, end / total * width);
            var rect = new Rect(left, 0.75, Math.Max(2, right - left), height - 1.5);
            dc.DrawRoundedRectangle(ViewportFill, ViewportPen, rect, 4, 4);
        }

        if (PlayheadSeconds is { } playhead)
        {
            var x = Math.Clamp(playhead / total * width, 0, width);
            dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, height));
        }

        if (ShowLiveEdge)
        {
            dc.DrawRectangle(Palette.TextBrush, null, new Rect(width - 2, 0, 2, height));
        }
    }

    /// <summary>
    /// Pack overlapping marks onto two rows. With overlap off nothing ever
    /// collides, so everything lands on row 0 and the lane stays one row tall.
    /// </summary>
    private Dictionary<long, int> AssignRows()
    {
        var rows = new Dictionary<long, int>();
        if (!AllowTwoRows)
        {
            foreach (var mark in _marks) rows[mark.Id] = 0;
            return rows;
        }

        var rowEnds = new List<double> { double.NegativeInfinity, double.NegativeInfinity };
        foreach (var mark in _marks.OrderBy(m => m.StartSeconds))
        {
            var row = mark.StartSeconds >= rowEnds[0] ? 0 : 1;
            rowEnds[row] = mark.EndSeconds;
            rows[mark.Id] = row;
        }
        return rows;
    }

    private static Rect Inflate(Rect rect, double by) =>
        new(rect.X - by, Math.Max(0, rect.Y - by), rect.Width + by * 2, rect.Height + by * 2);

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
