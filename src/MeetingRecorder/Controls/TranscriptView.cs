using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetingRecorder.Models;
using MeetingRecorder.Theme;
using MeetingRecorder.Views;

namespace MeetingRecorder.Controls;

/// <summary>
/// The live transcript strip on the recording screen: three lines of
/// recognised speech, with everything said earlier scrollable above them.
///
/// Three lines is deliberate. The operator's job is marking, not reading — the
/// strip is there to confirm that recognition is alive and roughly on time,
/// and a taller one would compete with the speaker grid for both space and
/// attention. History stays reachable for the moment someone asks "what did
/// they just say", and the view stops following the newest line as soon as
/// the operator scrolls back, so reading is never yanked out from under them.
///
/// Each line's timecode is tinted with the colour of whoever was marked when
/// it was spoken, which makes the mapping the Markdown will do visible while
/// there is still time to correct it.
///
/// No whisper dependency — this is a plain WPF control, so it compiles in the
/// Lite edition too even though nothing there ever shows it.
/// </summary>
public sealed class TranscriptView : Border
{
    /// <summary>Enough to show three lines; the rest scrolls.</summary>
    public const double StripHeight = 74;

    /// <summary>
    /// Long meetings produce a lot of lines, and every one is a live WPF
    /// element. The strip is a monitor, not the transcript — the file on disk
    /// is the record — so the oldest lines are retired.
    /// </summary>
    private const int MaxLines = 400;

    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _placeholder;
    private readonly Button _jumpToLive;

    /// <summary>
    /// Each drawn row's segment alongside the timecode label whose colour
    /// came from it, kept in the same order as <see cref="_lines"/>'
    /// children. What makes <see cref="RecolorAll"/> possible: a line's
    /// colour is otherwise baked in once at <see cref="Append"/> time and
    /// never touched again, which is correct for new lines (resolved against
    /// the segment's own historical timestamp) but goes stale if the mark it
    /// was resolved against is later edited — reassigned, nudged, merged,
    /// undone — in the Marks dock.
    /// </summary>
    private readonly List<(TranscriptSegment Segment, TextBlock TimeLabel)> _rows = new();

    private bool _following = true;

    public TranscriptView()
    {
        Background = Palette.WellBrush;
        BorderBrush = Palette.HairlineSoftBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        SnapsToDevicePixels = true;
        Height = StripHeight;

        _placeholder = Ui.Text("listening — recognised speech appears here a few seconds behind the room",
            12, Palette.TextMutedBrush);
        _placeholder.Margin = new Thickness(10, 6, 10, 6);

        _lines.Margin = new Thickness(10, 5, 10, 5);

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _lines,
        };
        _jumpToLive = Ui.MakeButton("↓ live", null, "ChipButtonAccent", (_, _) =>
        {
            _following = true;
            _scroller.ScrollToEnd();
        });
        _jumpToLive.HorizontalAlignment = HorizontalAlignment.Right;
        _jumpToLive.VerticalAlignment = VerticalAlignment.Bottom;
        _jumpToLive.Margin = new Thickness(0, 0, 12, 6);
        _jumpToLive.Visibility = Visibility.Collapsed;

        // Registered after the button exists, because the handler touches it
        // and a layout pass can raise this before anything is on screen.
        // Scrolling back is a deliberate act, so it turns following off;
        // reaching the bottom again turns it back on without a click.
        _scroller.ScrollChanged += (_, e) =>
        {
            // An extent change is the view growing under a new line, not the
            // operator moving; only the latter should stop it following.
            if (e.ExtentHeightChange != 0) return;
            _following = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 2;
            _jumpToLive.Visibility = _following ? Visibility.Collapsed : Visibility.Visible;
        };

        var stack = new Grid();
        stack.Children.Add(_placeholder);
        stack.Children.Add(_scroller);
        stack.Children.Add(_jumpToLive);
        _scroller.Visibility = Visibility.Collapsed;

        Child = stack;
    }

    /// <summary>
    /// Append one recognised line. <paramref name="slotColour"/> is the colour
    /// of the speaker marked when it was spoken, or null when nobody was.
    /// </summary>
    public void Append(TranscriptSegment segment, Color? slotColour)
    {
        if (segment.Text.Length == 0) return;

        if (_scroller.Visibility != Visibility.Visible)
        {
            _placeholder.Visibility = Visibility.Collapsed;
            _scroller.Visibility = Visibility.Visible;
        }

        var time = Ui.Mono(Ui.Clock(segment.StartSeconds), 11,
            slotColour is { } colour ? new SolidColorBrush(colour) : Palette.TextFaintBrush);
        time.Width = 62;
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 2, 8, 0);

        var text = Ui.Wrap(segment.Text, 13, Palette.TextSecondaryBrush);

        var row = Ui.Columns(1, time, text);
        row.Margin = new Thickness(0, 0, 0, 3);
        _lines.Children.Add(row);
        _rows.Add((segment, time));

        while (_lines.Children.Count > MaxLines)
        {
            _lines.Children.RemoveAt(0);
            _rows.RemoveAt(0);
        }

        if (_following) Dispatcher.InvokeAsync(_scroller.ScrollToEnd);
    }

    /// <summary>
    /// Re-resolve every drawn line's timecode colour against the marks as
    /// they stand right now. Called after a live-repair edit — reassign,
    /// nudge, merge, split, delete, undo/redo — so a line drawn against a
    /// mark that has since changed doesn't keep showing the colour it was
    /// given when it first appeared. Cheap: only the small timecode labels
    /// repaint, not the lines' layout or text.
    /// </summary>
    public void RecolorAll(Func<TranscriptSegment, Color?> resolve)
    {
        foreach (var (segment, label) in _rows)
        {
            label.Foreground = resolve(segment) is { } colour
                ? new SolidColorBrush(colour)
                : Palette.TextFaintBrush;
        }
    }

    /// <summary>
    /// Recognition could not start. The strip stays where it is rather than
    /// disappearing: the space is already spent, and an explained blank is
    /// better than one that reads as recognition silently doing nothing.
    /// </summary>
    public void ShowUnavailable(string reason)
    {
        _scroller.Visibility = Visibility.Collapsed;
        _jumpToLive.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
        _placeholder.Text = reason;
        _placeholder.Foreground = Palette.WarnBrush;
    }

    /// <summary>Drop every line — used when a recovered session's text is re-seeded.</summary>
    public void Clear()
    {
        _lines.Children.Clear();
        _rows.Clear();
        _scroller.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
    }
}
