using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingRecorder.Models;
using MeetingRecorder.Theme;
using MeetingRecorder.Views;

namespace MeetingRecorder.Controls;

/// <summary>
/// The live transcript strip on the recording screen: six lines of
/// recognised speech, with everything said earlier scrollable above them.
///
/// The operator's job is marking, not reading — the strip is there to confirm
/// that recognition is alive and roughly on time, and a much taller one would
/// compete with the speaker grid for both space and attention. History stays
/// reachable for the moment someone asks "what did they just say", and the
/// view stops following the newest line as soon as the operator scrolls back,
/// so reading is never yanked out from under them.
///
/// Each line's timecode is tinted with the colour of whoever was marked when
/// it was spoken, which makes the mapping the Markdown will do visible while
/// there is still time to correct it.
///
/// Clicking a line's text opens it for editing. Whisper is good but not
/// right, and a name or a piece of jargon it mangles is obvious to the person
/// in the room and unrecoverable to everyone downstream — the audio is
/// re-transcribable, the operator's memory of what was actually said is not.
/// Enter or clicking away commits, Esc abandons.
///
/// No whisper dependency — this is a plain WPF control, so it compiles in the
/// Lite edition too even though nothing there ever shows it.
/// </summary>
public sealed class TranscriptView : Border
{
    /// <summary>
    /// Enough to show six lines; the rest scrolls. Worked from the parts
    /// rather than eyeballed: 1px border top and bottom, the 5px padding the
    /// line stack carries each side, and six rows of a 13pt line (17.3px) plus
    /// the 3px gap under it — 133.8, rounded up for the same couple of pixels
    /// of slack the old five-line 116 had.
    /// </summary>
    public const double StripHeight = 136;

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
    private readonly List<Row> _rows = new();

    private bool _following = true;

    /// <summary>The row being edited, if any. Guards trimming and following.</summary>
    private Row? _editing;

    /// <summary>
    /// True while a line is open for editing. The recording screen checks it
    /// before treating a digit as a speaker key — the same rule the tile
    /// rename card follows.
    /// </summary>
    public bool IsEditing => _editing is not null;

    /// <summary>
    /// Raised when the operator finished editing a line and the text really
    /// changed. The segment has already been updated in place; the handler's
    /// job is to persist it.
    /// </summary>
    public event Action<TranscriptSegment>? TextEdited;

    /// <summary>One drawn line: the segment behind it and the elements showing it.</summary>
    private sealed class Row
    {
        public required TranscriptSegment Segment { get; init; }
        public required TextBlock TimeLabel { get; init; }
        public required TextBlock TextLabel { get; init; }
        public required Grid Container { get; init; }
    }

    public TranscriptView()
    {
        Background = Palette.WellBrush;
        BorderBrush = Palette.HairlineSoftBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        SnapsToDevicePixels = true;
        Height = StripHeight;

        _placeholder = Ui.Text("Listening — recognised speech appears here a few seconds behind the room",
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
        // The I-beam is the affordance; the standing hint beside the strip
        // says the rest. A tooltip here popped up over the live text the
        // operator was reading, and only once the pointer was already on the
        // line it described.
        text.Cursor = Cursors.IBeam;

        var row = Ui.Columns(1, time, text);
        row.Margin = new Thickness(0, 0, 0, 3);

        var entry = new Row { Segment = segment, TimeLabel = time, TextLabel = text, Container = row };
        text.MouseLeftButtonUp += (_, e) => { e.Handled = true; BeginEdit(entry); };

        _lines.Children.Add(row);
        _rows.Add(entry);

        // Never retire the line being corrected out from under the operator.
        while (_lines.Children.Count > MaxLines && !ReferenceEquals(_rows[0], _editing))
        {
            _lines.Children.RemoveAt(0);
            _rows.RemoveAt(0);
        }

        // Never scroll out from under a correction in progress — the line
        // being typed into has to stay where the operator put the cursor.
        if (_following && _editing is null) Dispatcher.InvokeAsync(_scroller.ScrollToEnd);
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
        foreach (var row in _rows)
        {
            row.TimeLabel.Foreground = resolve(row.Segment) is { } colour
                ? new SolidColorBrush(colour)
                : Palette.TextFaintBrush;
        }
    }

    /// <summary>
    /// Swap one line's text for an editable field, in place. The row keeps
    /// its position, its timecode and its colour, so the correction happens
    /// where the operator is already looking rather than in a dialog that
    /// would cover the grid they are marking from.
    /// </summary>
    private void BeginEdit(Row row)
    {
        if (_editing is not null) CommitEdit();

        var box = new TextBox
        {
            Text = row.Segment.Text,
            FontSize = 13,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
        };

        var committed = false;
        void Finish(bool keep)
        {
            if (committed) return;
            committed = true;
            EndEdit(row, keep ? box.Text : null);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);

        row.Container.Children.Remove(row.TextLabel);
        Grid.SetColumn(box, 1);
        row.Container.Children.Add(box);

        _editing = row;

        // Focus has to wait for the box to be in the visual tree, or it lands
        // nowhere and the first keystroke goes to the marking handler.
        Dispatcher.InvokeAsync(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    /// <summary>Put the label back, carrying the new text when there is one.</summary>
    private void EndEdit(Row row, string? text)
    {
        _editing = null;

        for (var i = row.Container.Children.Count - 1; i >= 0; i--)
        {
            if (row.Container.Children[i] is TextBox) row.Container.Children.RemoveAt(i);
        }

        var changed = false;
        if (text is not null)
        {
            var trimmed = text.Trim();
            // An emptied line is a refusal to answer, not a correction: the
            // recogniser heard something, and blanking the row would lose the
            // fact that it did. Left as it was.
            if (trimmed.Length > 0 && trimmed != row.Segment.Text)
            {
                row.Segment.Text = trimmed;
                row.TextLabel.Text = trimmed;
                changed = true;
            }
        }

        if (!row.Container.Children.Contains(row.TextLabel))
        {
            Grid.SetColumn(row.TextLabel, 1);
            row.Container.Children.Add(row.TextLabel);
        }

        // Hand keyboard focus back to the window rather than leaving it
        // nowhere. WPF routes key events to the focused element, so with focus
        // cleared the window's own PreviewKeyDown stops firing — Space, the
        // digits and every other shortcut went dead until something focusable
        // was clicked. The window is focusable, and a shortcut handled there
        // tunnels down as it always did.
        Dispatcher.InvokeAsync(() =>
        {
            if (Window.GetWindow(this) is not { } window) return;

            // Only when nothing else has taken it. The click that closed this
            // editor may have landed on a real field, and stealing focus back
            // from it would be worse than the bug being fixed.
            if (window.IsKeyboardFocusWithin) return;

            Keyboard.Focus(window);
        }, DispatcherPriority.Input);

        // Editing suppressed the auto-scroll, so the strip is still parked on
        // the corrected line. Put it back on the live edge, or the operator
        // fixes one word and never sees another one arrive.
        if (_following) Dispatcher.InvokeAsync(_scroller.ScrollToEnd);

        if (changed) TextEdited?.Invoke(row.Segment);
    }

    /// <summary>
    /// Commit whatever is open. Called when the strip loses the operator's
    /// attention for something that must not wait — stopping the recording,
    /// most of all, since the export reads the segment this is editing.
    /// </summary>
    public void CommitEdit()
    {
        if (_editing is null) return;

        // Moving focus fires LostKeyboardFocus, which commits through the
        // handler installed above rather than duplicating the logic here.
        // Focus goes to the window, never to nowhere: Keyboard.ClearFocus()
        // left the app with no focused element and therefore no keyboard
        // shortcuts at all.
        var row = _editing;
        var box = row.Container.Children.OfType<TextBox>().FirstOrDefault();
        if (box is not null && box.IsKeyboardFocusWithin && Window.GetWindow(this) is { } window)
        {
            Keyboard.Focus(window);
        }

        // Still open means the field never had focus to lose. Close it by
        // hand, keeping what was typed rather than discarding it.
        if (_editing is not null) EndEdit(row, box?.Text);
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
        _editing = null;
        _lines.Children.Clear();
        _rows.Clear();
        _scroller.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
    }
}
