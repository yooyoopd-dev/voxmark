using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// The whole transcript of a finished meeting, line by line, for review
/// before the Markdown is handed on. Opened from the export screen.
///
/// The live strip is for the moment — six lines, one correction at a time,
/// between marks. This is for afterwards, when the operator has the room and
/// the meeting is still fresh: every line with the speaker it will be filed
/// under, a search box to find the place somebody misspoke, and
/// find-and-replace for the name whisper got wrong the same way forty times.
/// Each line edits in place like the strip's do, and offers the same two
/// repairs — strike out, join onto the line above — because a hallucinated
/// line or a sentence split in two is as wrong after the meeting as during
/// it.
///
/// Every change lands in the session's segment list, which the Markdown is
/// built from, and is appended to <c>transcript.jsonl</c> through the same
/// store the recorder used, so reopening the session later shows the
/// corrected words. The marks are not editable here: who spoke is the
/// operator's record and has its own dock; this window is about what was
/// said.
///
/// No whisper dependency, so it builds into both editions — a session a Full
/// machine recorded can be reviewed and re-exported on a Lite one.
/// </summary>
public sealed class TranscriptEditorWindow : ShellWindow
{
    private readonly RecordingSession _session;
    private readonly TranscriptStore? _store;
    private readonly StackPanel _lines = new();
    private readonly ScrollViewer _scroller;
    private readonly TextBox _search;
    private readonly TextBox _replaceWith;
    private readonly TextBlock _status;
    private readonly TextBlock _count;
    private readonly List<Row> _rows = new();
    private Row? _editing;

    /// <summary>True once anything changed, so the caller knows to rewrite the Markdown.</summary>
    public bool Changed { get; private set; }

    private sealed class Row
    {
        public required TranscriptSegment Segment { get; init; }
        public required TextBlock TextLabel { get; init; }
        public required Grid Container { get; init; }
        public required StackPanel Actions { get; init; }
        public required Button MergeUp { get; init; }
    }

    public TranscriptEditorWindow(RecordingSession session)
        : base("Transcript — " + session.Title, 960, 760)
    {
        _session = session;
        MinWidth = 640;
        MinHeight = 480;
        Bar.CanMaximise = true;

        try
        {
            _store = new TranscriptStore(session.TranscriptPath);
        }
        catch (Exception ex)
        {
            // Corrections still apply to this export; they just will not
            // survive a reopen. Said in the status line rather than refused.
            AppPaths.Note("The transcript journal could not be opened for corrections: " + ex.Message);
        }

        _search = new TextBox { FontSize = 13, MinWidth = 220, MinHeight = 30 };
        _replaceWith = new TextBox { FontSize = 13, MinWidth = 220, MinHeight = 30 };
        _status = Ui.Wrap("", 12, Palette.TextMutedBrush);
        _count = Ui.Text("", 12.5, Palette.TextDimBrush);

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _lines,
            Padding = new Thickness(0, 4, 8, 4),
        };

        SetBody(BuildBody());
        BuildRows();
        RefreshCount();

        _search.TextChanged += (_, _) => ApplyFilter();

        PreviewKeyDown += (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.F)
            {
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _editing is null && !_search.IsKeyboardFocused && !_replaceWith.IsKeyboardFocused)
            {
                Close();
                e.Handled = true;
            }
        };

        // A click anywhere but the open line commits it, as on the recording
        // screen — the operator moved on, and the text they left is theirs.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_editing is null) return;
            if (e.OriginalSource is DependencyObject source && IsInside(source, _editing.Container)) return;
            CommitEdit();
        };

        Closing += (_, _) => CommitEdit();
        Closed += (_, _) => _store?.Dispose();
    }

    private UIElement BuildBody()
    {
        var heading = Ui.Text("Review the transcript", 22);
        var blurb = Ui.Wrap(
            "Click a line to correct it; Enter keeps the change, Esc drops it, Tab moves to the next line. " +
            "✕ strikes a line out, ⤒ joins it onto the line above. The speaker beside each line is who it " +
            "will be filed under in the Markdown — change that in the recording's marks, not here.",
            13, Palette.TextDimBrush);
        blurb.Margin = new Thickness(0, 6, 0, 16);

        var replaceAll = Ui.MakeButton("Replace all", null, "ChipButtonAccent", (_, _) => ReplaceAll());
        replaceAll.MinHeight = 30;
        replaceAll.Margin = new Thickness(8, 0, 0, 0);

        var find = Ui.Text("Find", 12.5, Palette.TextDimBrush);
        find.Margin = new Thickness(0, 0, 8, 0);
        var with = Ui.Text("Replace with", 12.5, Palette.TextDimBrush);
        with.Margin = new Thickness(16, 0, 8, 0);

        var tools = Ui.Columns(5, find, _search, with, _replaceWith, replaceAll, Ui.Filler(), _count);
        tools.Margin = new Thickness(0, 0, 0, 10);

        var list = Ui.Well(_scroller, new Thickness(12, 4, 4, 4));

        var done = Ui.MakeButton("Done", null, "AccentButton", (_, _) => Close());
        var footer = Ui.Columns(0, _status, done);
        footer.Margin = new Thickness(0, 14, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 16, 0);

        var body = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        var top = Ui.Vertical(0, heading, blurb, tools);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(footer);
        body.Children.Add(list);
        return body;
    }

    private void BuildRows()
    {
        _lines.Children.Clear();
        _rows.Clear();

        if (_session.Transcript.Count == 0)
        {
            _lines.Children.Add(Ui.Text("No speech was recognised in this session.", 13, Palette.TextMutedBrush));
            return;
        }

        foreach (var segment in _session.Transcript.OrderBy(s => s.StartSeconds))
        {
            if (segment.Text.Trim().Length == 0) continue;
            AddRow(segment);
        }
    }

    private void AddRow(TranscriptSegment segment)
    {
        var mark = TranscriptMapper.MarkFor(segment, _session.Marks);
        var colour = mark is not null ? Palette.BrushForSlot(mark.SpeakerSlot) : Palette.TextFaintBrush;

        var time = Ui.Mono(Ui.Clock(segment.StartSeconds), 11.5, colour);
        time.Width = 66;
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 3, 6, 0);

        var speaker = Ui.Text(mark is not null
            ? _session.SpeakerForSlot(mark.SpeakerSlot)?.Name ?? "Unknown"
            : "unmarked", 12, mark is not null ? colour : Palette.TextFaintBrush);
        speaker.Width = 110;
        speaker.VerticalAlignment = VerticalAlignment.Top;
        speaker.Margin = new Thickness(0, 2, 10, 0);

        var text = Ui.Wrap(segment.Text, 13.5, Palette.TextSecondaryBrush);
        text.Cursor = Cursors.IBeam;

        var strike = Ui.MakeButton("✕", null, "ChipButton");
        strike.ToolTip = "Strike this line out — nobody said it";
        var mergeUp = Ui.MakeButton("⤒", null, "ChipButton");
        mergeUp.ToolTip = "Join onto the line above";
        foreach (var button in new[] { strike, mergeUp })
        {
            button.MinHeight = 24;
            button.Padding = new Thickness(8, 0, 8, 0);
            button.FontSize = 11.5;
            button.VerticalAlignment = VerticalAlignment.Top;
            button.Focusable = false;
        }
        mergeUp.Margin = new Thickness(0, 0, 4, 0);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(8, 0, 0, 0),
        };
        actions.Children.Add(mergeUp);
        actions.Children.Add(strike);

        var row = Ui.Columns(2, time, speaker, text, actions);
        row.Margin = new Thickness(0, 0, 0, 6);

        var entry = new Row { Segment = segment, TextLabel = text, Container = row, Actions = actions, MergeUp = mergeUp };
        text.MouseLeftButtonUp += (_, e) => { e.Handled = true; BeginEdit(entry); };
        strike.Click += (_, _) => Delete(entry);
        mergeUp.Click += (_, _) => MergeUp(entry);

        _lines.Children.Add(row);
        _rows.Add(entry);
    }

    // ---------------------------------------------------------------- editing

    private void BeginEdit(Row row)
    {
        if (_editing is not null) CommitEdit();
        if (!_rows.Contains(row)) return;

        var box = new TextBox
        {
            Text = row.Segment.Text,
            FontSize = 13.5,
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

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
            else if (e.Key == Key.Tab)
            {
                e.Handled = true;
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                var next = NextVisible(_rows.IndexOf(row), step);
                Finish(true);
                if (next is not null)
                {
                    BeginEdit(next);
                    next.Container.BringIntoView();
                }
            }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);

        row.Container.Children.Remove(row.TextLabel);
        Grid.SetColumn(box, 2);
        row.Container.Children.Add(box);

        row.MergeUp.IsEnabled = _rows.IndexOf(row) > 0;
        row.Actions.Visibility = Visibility.Visible;
        _editing = row;

        Dispatcher.InvokeAsync(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    /// <summary>The next row in the given direction that the filter has not hidden.</summary>
    private Row? NextVisible(int from, int step)
    {
        for (var i = from + step; i >= 0 && i < _rows.Count; i += step)
        {
            if (_rows[i].Container.Visibility == Visibility.Visible) return _rows[i];
        }
        return null;
    }

    private void EndEdit(Row row, string? text)
    {
        _editing = null;
        row.Actions.Visibility = Visibility.Collapsed;

        for (var i = row.Container.Children.Count - 1; i >= 0; i--)
        {
            if (row.Container.Children[i] is TextBox) row.Container.Children.RemoveAt(i);
        }

        if (text is not null)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0 && trimmed != row.Segment.Text)
            {
                row.Segment.Text = trimmed;
                row.TextLabel.Text = trimmed;
                Journal(() => _store?.AppendEdit(row.Segment));
                Say("Corrected the line at " + Ui.Clock(row.Segment.StartSeconds) + ".");
            }
        }

        if (!row.Container.Children.Contains(row.TextLabel))
        {
            Grid.SetColumn(row.TextLabel, 2);
            row.Container.Children.Add(row.TextLabel);
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (!IsKeyboardFocusWithin) Keyboard.Focus(this);
        }, DispatcherPriority.Input);
    }

    private void CommitEdit()
    {
        if (_editing is null) return;
        var row = _editing;
        var box = row.Container.Children.OfType<TextBox>().FirstOrDefault();
        if (box is not null && box.IsKeyboardFocusWithin) Keyboard.Focus(this);
        if (_editing is not null) EndEdit(row, box?.Text);
    }

    private void Delete(Row row)
    {
        if (ReferenceEquals(_editing, row)) EndEdit(row, null);
        if (!Remove(row)) return;

        _session.Transcript.Remove(row.Segment);
        Journal(() => _store?.AppendDelete(row.Segment));
        RefreshCount();
        Say("Struck out the line at " + Ui.Clock(row.Segment.StartSeconds) + ".");
    }

    private void MergeUp(Row row)
    {
        var index = _rows.IndexOf(row);
        if (index <= 0) return;
        if (ReferenceEquals(_editing, row)) CommitEdit();
        if (!_rows.Contains(row)) return;

        var above = _rows[index - 1];
        var joined = (above.Segment.Text.TrimEnd() + " " + row.Segment.Text.TrimStart()).Trim();
        above.Segment.Text = joined;
        above.Segment.EndSeconds = Math.Max(above.Segment.EndSeconds, row.Segment.EndSeconds);
        above.TextLabel.Text = joined;

        Remove(row);
        _session.Transcript.Remove(row.Segment);
        Journal(() =>
        {
            _store?.AppendEdit(above.Segment);
            _store?.AppendDelete(row.Segment);
        });
        RefreshCount();
        Say("Joined the line at " + Ui.Clock(row.Segment.StartSeconds) + " onto the one above.");
    }

    private bool Remove(Row row)
    {
        var index = _rows.IndexOf(row);
        if (index < 0) return false;
        _rows.RemoveAt(index);
        _lines.Children.Remove(row.Container);
        return true;
    }

    // --------------------------------------------------------- find & replace

    /// <summary>Hide every line that does not contain the search text. Case does not matter.</summary>
    private void ApplyFilter()
    {
        var needle = _search.Text.Trim();
        var shown = 0;
        foreach (var row in _rows)
        {
            var visible = needle.Length == 0 ||
                          row.Segment.Text.Contains(needle, StringComparison.OrdinalIgnoreCase);
            row.Container.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) shown++;
        }

        _status.Text = needle.Length == 0 ? "" : shown + " of " + _rows.Count + " lines contain \"" + needle + "\"";
        _status.Foreground = Palette.TextMutedBrush;
    }

    /// <summary>
    /// Replace the search text wherever it appears, in every line at once.
    /// The one repair that only makes sense after the meeting: a name whisper
    /// mis-hears is mis-heard the same way every time, and fixing forty lines
    /// by hand is forty chances to miss one. Case-insensitive match, and the
    /// replacement is typed exactly, so it also fixes capitalisation.
    /// </summary>
    private void ReplaceAll()
    {
        CommitEdit();

        var find = _search.Text;
        var with = _replaceWith.Text;
        if (find.Trim().Length == 0)
        {
            Say("Type the word to find first.", warn: true);
            return;
        }
        if (string.Equals(find, with, StringComparison.Ordinal))
        {
            Say("Find and Replace with are the same.", warn: true);
            return;
        }

        var changed = 0;
        foreach (var row in _rows)
        {
            var text = row.Segment.Text;
            if (!text.Contains(find, StringComparison.OrdinalIgnoreCase)) continue;

            var replaced = text.Replace(find, with, StringComparison.OrdinalIgnoreCase).Trim();
            // Replacing the only words on a line with nothing would blank it;
            // that is a strike-out, which has its own button and its own
            // record in the journal.
            if (replaced.Length == 0 || replaced == text) continue;

            row.Segment.Text = replaced;
            row.TextLabel.Text = replaced;
            Journal(() => _store?.AppendEdit(row.Segment));
            changed++;
        }

        Say(changed == 0
            ? "Nothing to replace."
            : "Replaced \"" + find + "\" in " + changed + (changed == 1 ? " line." : " lines."));
        ApplyFilter();
        RefreshCount();
    }

    // ------------------------------------------------------------------ misc

    private void Journal(Action write)
    {
        Changed = true;
        try
        {
            write();
        }
        catch (Exception ex)
        {
            AppPaths.Note("Could not journal a transcript correction: " + ex.Message);
        }
    }

    private void RefreshCount()
    {
        var words = TranscriptMapper.WordCount(_session.Transcript);
        _count.Text = _rows.Count.ToString("N0") + " lines · " + words.ToString("N0") + " words";
    }

    private void Say(string message, bool warn = false)
    {
        _status.Text = message;
        _status.Foreground = warn ? Palette.WarnBrush : Palette.TextMutedBrush;
    }

    private static bool IsInside(DependencyObject source, DependencyObject ancestor)
    {
        for (var node = source; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, ancestor)) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
    {
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            if (VisualTreeHelper.GetParent(node) is { } visual) return visual;
        }
        return LogicalTreeHelper.GetParent(node);
    }
}
