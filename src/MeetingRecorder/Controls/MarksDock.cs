using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;
using MeetingRecorder.Views;

namespace MeetingRecorder.Controls;

/// <summary>Which boundary the arrow keys nudge.</summary>
public enum MarkBoundary
{
    Start,
    End,
}

/// <summary>
/// S4 · the marks dock — design guide section 06. It grows from the bottom
/// of the recording window rather than being a separate page, and the
/// recording UI never disappears while it is open.
///
/// The rules it implements, from the guide's own "dock rules":
///   - No modals, ever. Reassign is an inline dropdown; delete is immediate
///     with a 6-second undo toast.
///   - 0.5 s nudge step, 0.1 s fine. Start can never pass end. If an edit
///     overlaps a neighbour, the neighbour is trimmed and a toast says which.
///   - The app flags its own suspects: marks under 2 s and sub-0.3 s gaps
///     between different speakers get a red hairline and a one-line hint.
///     The guide calls this the single most valuable feature for transcript
///     quality.
///   - Editing never pauses the recorder.
/// </summary>
public sealed class MarksDock : Border
{
    /// <summary>
    /// Two rows collapsed, not three. The dock's collapsed job is the
    /// last-few-seconds repair — "I pressed that a beat late" — and two rows
    /// cover it; the third was reference, not reach, and the transcript strip
    /// above needs that height more. Expanding still shows everything.
    /// </summary>
    private const int CollapsedRowCount = 2;

    /// <summary>Collapsed chrome is trimmed everywhere it can be without crowding the row.</summary>
    private static readonly Thickness CollapsedHeaderMargin = new(20, 8, 20, 6);
    private static readonly Thickness ExpandedHeaderMargin = new(20, 12, 20, 10);
    private static readonly Thickness CollapsedRowsMargin = new(20, 0, 20, 8);
    private static readonly Thickness ExpandedRowsMargin = new(20, 0, 20, 14);

    private readonly MarkingEngine _engine;
    private readonly RecordingSession _session;

    private readonly TextBlock _heading;
    private readonly TextBlock _subheading;
    private readonly StackPanel _filterChips = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _rowsHost = new();
    private readonly ScrollViewer _scroller;
    private readonly BlockLaneView _lane;
    private readonly Border _laneHost;
    private readonly Grid _header;
    private readonly Border _rowsHolder;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly Button _toastAction;
    private readonly Button _expandButton;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>
    /// The live rows on screen and the label that counts each one up, so the
    /// tick can keep them running without rebuilding the dock ten times a
    /// second. Cleared and refilled by <see cref="Refresh"/>.
    /// </summary>
    private readonly List<(OpenMark Open, TextBlock Elapsed)> _liveRows = new();

    private string _filter = "all";
    private bool _expanded;
    private long? _editingId;

    public MarksDock(MarkingEngine engine, RecordingSession session)
    {
        _engine = engine;
        _session = session;

        Background = Palette.ChromeBrush;
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xE9, 0xE9, 0xED));
        BorderThickness = new Thickness(0, 1, 0, 0);

        _heading = Ui.Section("Marks · live repair", Palette.AccentBrush);
        _subheading = Ui.Text("recording continues while you edit", 12, Palette.TextMutedBrush);

        _expandButton = Ui.MakeButton("Expand ⌃", "Ctrl+E", "ChipButtonAccent", (_, _) => Toggle());

        _lane = new BlockLaneView { AllowTwoRows = session.Options.AllowOverlappingMarks, ShowLiveEdge = true };
        _lane.MarkClicked += id => OpenRow(id);
        _lane.Scrubbed += seconds =>
        {
            PlayheadSeconds = seconds;
            Refresh();
        };
        _laneHost = Ui.Well(_lane, new Thickness(0), 6);
        _laneHost.Height = 34;
        _laneHost.Margin = new Thickness(20, 10, 20, 0);
        _laneHost.Visibility = Visibility.Collapsed;

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rowsHost,
        };

        _toastText = Ui.Text("", 12.5, Palette.TextSecondaryBrush);
        _toastAction = Ui.MakeButton("Undo", "Ctrl+Z", "ChipButtonAccent", (_, _) =>
        {
            _engine.Undo();
            HideToast();
        });
        _toast = Ui.Card(Ui.Columns(0, _toastText, _toastAction), new Thickness(12, 8, 8, 8));
        _toast.Margin = new Thickness(20, 0, 20, 8);
        _toast.Visibility = Visibility.Collapsed;
        _toastTimer.Tick += (_, _) => HideToast();

        _header = Ui.Columns(2, Pad(_heading, 0, 12), _subheading, Ui.Filler(), _filterChips, _expandButton);
        _header.Margin = CollapsedHeaderMargin;

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_laneHost, Dock.Top);
        DockPanel.SetDock(_toast, Dock.Bottom);
        body.Children.Add(_header);
        body.Children.Add(_laneHost);
        body.Children.Add(_toast);

        _rowsHolder = new Border { Margin = CollapsedRowsMargin, Child = _scroller };
        body.Children.Add(_rowsHolder);

        Child = body;

        BuildFilterChips();
        Refresh();
    }

    /// <summary>Selected row; also what the arrow keys and the editor act on.</summary>
    public long? SelectedMarkId { get; private set; }

    /// <summary>Which boundary ← and → move.</summary>
    public MarkBoundary Boundary { get; private set; } = MarkBoundary.Start;

    /// <summary>Review position set by clicking the lane; "split at playhead" uses it.</summary>
    public double? PlayheadSeconds { get; private set; }

    public bool IsExpanded => _expanded;

    /// <summary>Raised when the dock needs the window to re-measure or re-render.</summary>
    public event Action? LayoutChanged;

    public void Toggle()
    {
        _expanded = !_expanded;
        _laneHost.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _filterChips.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _expandButton.Content = _expanded ? "Collapse ⌄" : "Expand ⌃";
        _header.Margin = _expanded ? ExpandedHeaderMargin : CollapsedHeaderMargin;
        _rowsHolder.Margin = _expanded ? ExpandedRowsMargin : CollapsedRowsMargin;
        if (!_expanded) _editingId = null;
        Refresh();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Cheap per-frame update: the lane has to keep growing with the
    /// recording and the open mark's row has to keep counting, but rebuilding
    /// every row ten times a second would not.
    /// </summary>
    public void UpdateLive(double seconds)
    {
        foreach (var (open, elapsed) in _liveRows)
        {
            elapsed.Text = "◉ " + Math.Max(0, seconds - open.StartSeconds).ToString("0.0") + " s";
        }

        if (!_expanded) return;
        _lane.TotalSeconds = Math.Max(1, seconds);
        _lane.InvalidateVisual();
    }

    public void ShowNotice(string message, bool offerUndo)
    {
        _toastText.Text = message;
        _toastAction.Visibility = offerUndo ? Visibility.Visible : Visibility.Collapsed;
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toast.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------- keyboard

    public void MoveSelection(int delta)
    {
        var marks = VisibleMarks();
        if (marks.Count == 0) return;

        var index = SelectedMarkId is { } id ? marks.FindIndex(m => m.Id == id) : -1;
        index = index < 0 ? 0 : Math.Clamp(index + delta, 0, marks.Count - 1);
        SelectedMarkId = marks[index].Id;
        Refresh();
    }

    /// <summary>Enter opens the selected row for editing — one row at a time.</summary>
    public void OpenSelected()
    {
        if (SelectedMarkId is { } id) OpenRow(id);
    }

    public void ToggleBoundary()
    {
        Boundary = Boundary == MarkBoundary.Start ? MarkBoundary.End : MarkBoundary.Start;
        Refresh();
    }

    /// <summary>← → nudge 0.5 s, Shift+← → 0.1 s.</summary>
    public void Nudge(double delta)
    {
        if (SelectedMarkId is not { } id) return;

        var moved = Boundary == MarkBoundary.Start
            ? _engine.NudgeStart(id, delta)
            : _engine.NudgeEnd(id, delta);

        if (!moved) ShowNotice("That boundary cannot move any further", false);
    }

    public void DeleteSelected()
    {
        if (SelectedMarkId is not { } id) return;
        var mark = _engine.ById(id);
        if (mark is null) return;

        var name = _session.SpeakerForSlot(mark.SpeakerSlot)?.Name ?? "This";
        if (_engine.Delete(id))
        {
            SelectedMarkId = null;
            _editingId = null;
            // Immediate, with a 6-second undo toast — never a confirmation
            // dialog in the middle of a meeting.
            ShowNotice("Deleted " + name + "'s mark", true);
        }
    }

    private void OpenRow(long id)
    {
        SelectedMarkId = id;
        _editingId = id;
        if (!_expanded) Toggle();
        else Refresh();
    }

    // --------------------------------------------------------------- render

    public void Refresh()
    {
        var all = _engine.NewestFirst();

        _heading.Text = _expanded ? "MARKS · " + all.Count : "MARKS · LIVE REPAIR";
        _subheading.Text = _expanded
            ? "newest first · one row open at a time"
            : "who is marked right now — reassign without stopping";

        _lane.TotalSeconds = Math.Max(1, _engine.CurrentFileSeconds);
        _lane.SelectedMarkId = SelectedMarkId;
        _lane.PlayheadSeconds = PlayheadSeconds;
        _lane.SetMarks(_engine.Chronological());

        _rowsHost.Children.Clear();
        _liveRows.Clear();

        // Collapsed, the dock answers one question — "who is this going to,
        // and is that right?" — so the mark that is open right now leads,
        // even though it is not in the marks list yet. Without it the row an
        // operator most wants to correct is the one row the dock never shows.
        var live = _expanded
            ? Array.Empty<OpenMark>()
            : _engine.Open.OrderByDescending(o => o.StartSeconds).Take(CollapsedRowCount).ToArray();

        foreach (var open in live) _rowsHost.Children.Add(LiveRow(open));

        var visible = VisibleMarks(CollapsedRowCount - live.Length);

        if (visible.Count == 0 && live.Length == 0)
        {
            _rowsHost.Children.Add(Ui.Text(
                _expanded ? "No marks match this filter yet" : "No marks yet — press a speaker's key when they start",
                12.5, Palette.TextMutedBrush));
            return;
        }

        foreach (var mark in visible)
        {
            _rowsHost.Children.Add(_expanded && _editingId == mark.Id ? Editor(mark) : Row(mark));
        }
    }

    private List<Mark> VisibleMarks(int collapsedRoom = CollapsedRowCount)
    {
        var marks = _engine.NewestFirst().ToList();
        if (!_expanded) return marks.Take(Math.Max(0, collapsedRoom)).ToList();

        return _filter switch
        {
            "recent" => marks.Where(m => m.EndSeconds >= _engine.CurrentFileSeconds - 300).ToList(),
            "short" => marks.Where(m => _engine.IsShort(m)).ToList(),
            _ => marks,
        };
    }

    private void BuildFilterChips()
    {
        _filterChips.Children.Clear();
        AddFilterChip("All", "all");
        AddFilterChip("Last 5 min", "recent");
        AddFilterChip("Under 2 s", "short");
        _filterChips.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFilterChip(string label, string key)
    {
        var chip = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current?.TryFindResource(_filter == key ? "ChipButtonActive" : "ChipButton") is Style style)
        {
            chip.Style = style;
        }
        chip.Click += (_, _) =>
        {
            _filter = key;
            BuildFilterChips();
            Refresh();
        };
        _filterChips.Children.Add(chip);
    }

    private UIElement Row(Mark mark)
    {
        var colour = Palette.ForSlot(mark.SpeakerSlot);
        var speaker = _session.SpeakerForSlot(mark.SpeakerSlot);
        var suspect = _engine.SuspectReason(mark);
        var selected = SelectedMarkId == mark.Id;

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(colour),
            Margin = new Thickness(0, 0, 12, 0),
        };

        var key = Ui.Mono(speaker?.KeyLabel ?? "?", 11, Palette.TextMutedBrush);
        key.Width = 22;

        var name = Ui.Text(speaker?.Name ?? "Unknown", 14);
        name.Width = 130;
        name.Margin = new Thickness(0, 0, 12, 0);

        var range = Ui.Mono(Ui.Tenths(mark.StartSeconds) + " → " + Ui.Tenths(mark.EndSeconds), 13);
        range.Width = 210;
        range.Margin = new Thickness(0, 0, 12, 0);

        var duration = Ui.Mono(mark.DurationSeconds.ToString("0.0") + " s", 12.5,
            suspect is null ? Palette.TextMutedBrush : Palette.RecBrush);
        duration.Width = 56;

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (_expanded)
        {
            trailing.Children.Add(Ui.Text(suspect ?? "click to edit", 12,
                suspect is null ? Palette.TextMutedBrush : Palette.RecBrush));
        }
        else
        {
            // Reassign and nothing else. The boundary nudges and the delete
            // that used to sit here are a keystroke away (← →, Ctrl+Z) and a
            // click away in the expanded dock; on a two-row strip glanced at
            // mid-meeting they were four targets to hit and three of them
            // changed the timeline. Who a turn belongs to is the correction
            // that has to be right before the meeting moves on.
            trailing.Children.Add(ReassignDropdown(mark));
        }

        // bar · key · name · range · duration · slack · actions
        var row = Ui.Columns(5, bar, key, name, range, duration, Ui.Filler(), trailing);

        var card = new Border
        {
            Background = Palette.SurfaceBrush,
            CornerRadius = new CornerRadius(6),
            Padding = _expanded ? new Thickness(12, 9, 12, 9) : new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, _expanded ? 6 : 4),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            // A red hairline is the suspect flag; selection wins over it so
            // the operator can always see what they are about to edit.
            BorderBrush = selected ? Palette.AccentBrush
                : suspect is not null ? Palette.RecEdgeBrush
                : Palette.SurfaceBrush,
            Cursor = Cursors.Hand,
            Child = row,
        };
        card.MouseLeftButtonUp += (_, _) => OpenRow(mark.Id);
        return card;
    }

    /// <summary>
    /// The mark that is open right now, as a row. It has no id and cannot be
    /// nudged, split or deleted — it has no end yet — so the only thing it
    /// offers is the one thing that is wrong often enough to matter mid-turn:
    /// who it belongs to.
    /// </summary>
    private UIElement LiveRow(OpenMark open)
    {
        var colour = Palette.ForSlot(open.SpeakerSlot);
        var speaker = _session.SpeakerForSlot(open.SpeakerSlot);

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(colour),
            Margin = new Thickness(0, 0, 12, 0),
        };

        var key = Ui.Mono(speaker?.KeyLabel ?? "?", 11, Palette.TextMutedBrush);
        key.Width = 22;

        var name = Ui.Text(speaker?.Name ?? "Unknown", 14);
        name.Width = 130;
        name.Margin = new Thickness(0, 0, 12, 0);

        var range = Ui.Mono(Ui.Tenths(open.StartSeconds) + " → still open", 13);
        range.Width = 210;
        range.Margin = new Thickness(0, 0, 12, 0);

        var elapsed = Ui.Mono("◉ 0.0 s", 12.5, new SolidColorBrush(colour));
        elapsed.Width = 74;
        _liveRows.Add((open, elapsed));

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        trailing.Children.Add(ReassignPicker(open.SpeakerSlot, slot => _engine.ReassignOpen(open.SpeakerSlot, slot)));

        var row = Ui.Columns(5, bar, key, name, range, elapsed, Ui.Filler(), trailing);

        return new Border
        {
            Background = Palette.TintBrush(colour, 0.12),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(colour),
            Child = row,
        };
    }

    private Dropdown ReassignDropdown(Mark mark) =>
        ReassignPicker(mark.SpeakerSlot, slot => _engine.Reassign(mark.Id, slot));

    /// <summary>
    /// Inline speaker picker — section 06 requires reassign to be inline and
    /// never a modal. The speaker it is currently assigned to is marked in
    /// the list rather than removed from it, so the roster keeps the same
    /// order and the same position every time it is opened.
    /// </summary>
    private Dropdown ReassignPicker(int currentSlot, Action<int> assign)
    {
        var dropdown = new Dropdown("ChipButton")
        {
            DisplayText = "reassign",
            MinHeight = 26,
            Margin = new Thickness(0, 0, 6, 0),
            PopupMinWidth = 180,
        };
        dropdown.SetItems(_session.Speakers.Select(s =>
            ((s.SlotIndex == currentSlot ? "● " : "   ") + s.KeyLabel + "  " + s.Name, (object)s.SlotIndex)));
        dropdown.SelectionChanged += value =>
        {
            if (value is int slot && slot != currentSlot) assign(slot);
            dropdown.DisplayText = "reassign";
        };
        return dropdown;
    }

    private UIElement Editor(Mark mark)
    {
        var colour = Palette.ForSlot(mark.SpeakerSlot);
        var speaker = _session.SpeakerForSlot(mark.SpeakerSlot);

        var bar = new Border
        {
            Width = 3,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(colour),
            Margin = new Thickness(0, 0, 12, 0),
        };

        var head = Ui.Columns(4,
            bar,
            Pad(Ui.Mono(speaker?.KeyLabel ?? "?", 11, Palette.TextMutedBrush), 0, 10),
            Pad(Ui.Text(speaker?.Name ?? "Unknown", 15), 0, 12),
            ReassignDropdown(mark),
            Ui.Filler(),
            Ui.Text("editing does not pause the recorder", 11.5, Palette.TextMutedBrush));

        var startField = BoundaryField("Start", Ui.Tenths(mark.StartSeconds), MarkBoundary.Start,
            delta => _engine.NudgeStart(mark.Id, delta));
        var endField = BoundaryField("End", Ui.Tenths(mark.EndSeconds), MarkBoundary.End,
            delta => _engine.NudgeEnd(mark.Id, delta));

        var durationField = Ui.Well(Ui.Vertical(0,
            Center(Ui.Text("Duration", 10, Palette.TextMutedBrush)),
            Center(Ui.Mono(mark.DurationSeconds.ToString("0.0") + " s", 15, Palette.TextBrush))),
            new Thickness(10, 8, 10, 8), 6);

        var fields = new Grid();
        for (var i = 0; i < 3; i++)
        {
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        startField.Margin = new Thickness(0, 0, 10, 0);
        endField.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(startField, 0);
        Grid.SetColumn(endField, 1);
        Grid.SetColumn(durationField, 2);
        fields.Children.Add(startField);
        fields.Children.Add(endField);
        fields.Children.Add(durationField);

        var audition = Ui.MakeButton("▶ Connect headphones to audition", null, "GhostButton");
        audition.IsEnabled = false;
        audition.ToolTip = "Playback would feed the room speakers back into the recording.";

        var split = Ui.MakeButton("⤈ Split at playhead", null, "GhostButton", (_, _) =>
        {
            if (PlayheadSeconds is { } at && _engine.Split(mark.Id, at))
            {
                ShowNotice("Split at " + Ui.Tenths(at), true);
            }
            else
            {
                ShowNotice("Click the lane above inside this mark first, then split", false);
            }
        });

        var merge = Ui.MakeButton("⤊ Merge with previous", null, "GhostButton", (_, _) =>
        {
            if (!_engine.MergeWithPrevious(mark.Id)) ShowNotice("Nothing before this mark to merge with", false);
        });

        var insert = Ui.MakeButton("＋ Insert mark before", null, "GhostButton", (_, _) =>
        {
            if (_engine.InsertBefore(mark.Id, mark.SpeakerSlot) is null)
            {
                ShowNotice("No gap in front of this mark to fill", false);
            }
            else
            {
                ShowNotice("Inserted a mark in the gap — reassign it if it was someone else", true);
            }
        });

        var remove = Ui.MakeButton("✕", null, "DangerButton", (_, _) =>
        {
            SelectedMarkId = mark.Id;
            DeleteSelected();
        });
        remove.MinWidth = 44;

        var actions = new Grid();
        var actionButtons = new UIElement[] { audition, split, merge, insert, remove };
        for (var i = 0; i < actionButtons.Length; i++)
        {
            actions.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == actionButtons.Length - 1 ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
            });
            if (actionButtons[i] is FrameworkElement element && i < actionButtons.Length - 1)
            {
                element.Margin = new Thickness(0, 0, 8, 0);
            }
            Grid.SetColumn(actionButtons[i], i);
            actions.Children.Add(actionButtons[i]);
        }

        var note = Ui.Wrap(
            "Audition needs headphones — with the room speakers active it would feed playback back into the recording, " +
            "so it stays disabled. Split uses the playhead you set by clicking the lane above.",
            11.5, Palette.TextMutedBrush);

        var card = Ui.Card(Ui.Vertical(12, head, fields, actions, note), new Thickness(14));
        card.BorderBrush = Palette.AccentBrush;
        card.BorderThickness = new Thickness(1.5);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private Border BoundaryField(string label, string value, MarkBoundary boundary, Func<double, bool> nudge)
    {
        var minus = StepButton("−", () => nudge(-MarkingEngine.NudgeStepSeconds));
        var plus = StepButton("+", () => nudge(MarkingEngine.NudgeStepSeconds));

        var centre = Ui.Vertical(0,
            Center(Ui.Text(label, 10, Palette.TextMutedBrush)),
            Center(Ui.Mono(value, 15, Palette.TextBrush)));

        var row = Ui.Columns(1, minus, centre, plus);
        var well = Ui.Well(row, new Thickness(10, 8, 10, 8), 6);

        // The focused boundary is the one the arrow keys move.
        if (Boundary == boundary)
        {
            well.BorderBrush = Palette.AccentBrush;
            well.BorderThickness = new Thickness(1.5);
        }
        well.Cursor = Cursors.Hand;
        well.MouseLeftButtonUp += (_, _) =>
        {
            Boundary = boundary;
            Refresh();
        };
        return well;
    }

    private Button StepButton(string glyph, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
        };
        if (Application.Current?.TryFindResource("GhostButton") is Style style) button.Style = style;
        button.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }

    private static FrameworkElement Center(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    private static FrameworkElement Pad(FrameworkElement element, double vertical, double right = 0)
    {
        element.Margin = new Thickness(0, vertical, right, vertical);
        return element;
    }
}
