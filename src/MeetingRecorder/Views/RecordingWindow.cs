using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeetingRecorder.Controls;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// S3 · Recording, with S4 growing from its own bottom edge — design guide
/// sections 04 to 06.
///
/// The first principle the whole screen is built around: the operator is
/// also a participant. They will look away, mis-click and forget to close a
/// mark, so recording never stops for any reason and every mistake is
/// repairable in under three seconds without leaving this screen. There is
/// no route out except Stop, and Stop always asks once.
/// </summary>
public sealed class RecordingWindow : ShellWindow
{
    private readonly RecordingSession _session;
    private readonly AudioCaptureService _capture;
    private readonly MarkingEngine _marking;
    private readonly MarkJournal _journal;
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly ToastWindow _toast = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ConcurrentQueue<WaveSlice> _slices = new();
    private readonly Dictionary<int, SpeakerTile> _tiles = new();

    private readonly Ellipse _recDot;
    private readonly TextBlock _clock;
    private readonly TextBlock _markCount;
    private readonly TextBlock _speakingNow;
    private readonly TextBlock _inputName;
    private readonly TextBlock _diskWritten;
    private readonly TextBlock _droppedLabel;
    private readonly TextBlock _partLabel;
    private readonly Button _pauseButton;
    private readonly WaveformView _waveform;
    private readonly Border _waveWell;
    private readonly BlockLaneView _minimap;
    private readonly TextBlock _minimapClock;
    private readonly UniformGrid _tileGrid = new();
    private readonly TextBlock _speakersHeading;
    private readonly TextBlock _rosterHint;
    private readonly Border _addStrip;
    private readonly TextBox _addName;
    private readonly TextBox _addRole;
    private readonly Border _confirmBanner;
    private readonly TextBlock _confirmText;
    private readonly Border _noticeBanner;
    private readonly TextBlock _noticeText;
    private readonly MarksDock _dock;
    private readonly Border _dockHost;

    private bool _awaitingStopConfirm;
    private bool _stopping;
    private DateTime? _pausedAt;
    private double _lastSavedAt;
    private double _blinkPhase;

    public RecordingWindow(RecordingSession session, int deviceNumber)
        : base("VoxMark — " + session.Title, 1360, 860)
    {
        _session = session;

        // Section 05: minimum window 1180 × 760. Below that the app refuses
        // to shrink further.
        MinWidth = 1180;
        MinHeight = 760;

        _capture = new AudioCaptureService(session.Options.Mp3BitrateKbps);
        _marking = new MarkingEngine(session.Options);
        _journal = new MarkJournal(session.JournalPath);
        _marking.Journal = _journal;
        _marking.SpeakerNameResolver = slot => session.SpeakerForSlot(slot)?.Name ?? ("slot " + (slot + 1));

        _recDot = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = Palette.RecBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            Effect = new DropShadowEffect { Color = Palette.Rec, BlurRadius = 12, ShadowDepth = 0, Opacity = 1 },
        };

        _clock = new TextBlock
        {
            Text = "00:00:00",
            FontFamily = Ui.MonoFont,
            FontSize = 38,
            Foreground = Palette.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _markCount = StatValue("0");
        _speakingNow = StatValue("nobody");
        _inputName = StatValue(session.InputDeviceName);
        _diskWritten = StatValue("0.0 MB");
        _droppedLabel = Ui.Text("", 12, Palette.WarnBrush);
        _partLabel = Ui.Text("", 12, Palette.AccentTextBrush);

        _pauseButton = Ui.MakeButton("⏸ Pause", "Ctrl+P", "GhostButton", (_, _) => TogglePause());

        _waveform = new WaveformView();
        _waveWell = Ui.Well(_waveform, new Thickness(0), 8);
        _waveWell.Height = 132;

        _minimap = new BlockLaneView { AllowTwoRows = session.Options.AllowOverlappingMarks, IsInteractive = false };
        _minimapClock = Ui.Mono("00:00:00 / whole session", 11, Palette.TextMutedBrush);

        _speakersHeading = Ui.Section("Speakers · " + session.Speakers.Count + " of 12");
        _rosterHint = Ui.Text(
            "keyboard is the primary control; clicking a tile is the same action. Opening a new speaker closes the previous one.",
            12, Palette.TextMutedBrush);

        _addName = InlineField("Name");
        _addRole = InlineField("Role");
        _addStrip = BuildAddStrip();

        _confirmText = Ui.Text("", 14, Palette.RecTextBrush);
        _confirmBanner = Ui.Card(_confirmText, new Thickness(14, 10, 14, 10), Palette.RecBrush);
        _confirmBanner.Visibility = Visibility.Collapsed;
        _confirmBanner.Margin = new Thickness(20, 12, 20, 0);

        _noticeText = Ui.Wrap("", 12.5, Palette.TextSecondaryBrush);
        _noticeBanner = Ui.Card(_noticeText, new Thickness(14, 10, 14, 10), Palette.WarnBrush);
        _noticeBanner.Visibility = Visibility.Collapsed;
        _noticeBanner.Margin = new Thickness(20, 12, 20, 0);

        _dock = new MarksDock(_marking, _session);
        _dockHost = new Border { Child = _dock, MaxHeight = 232 };
        _dock.LayoutChanged += OnDockLayoutChanged;

        SetBody(BuildBody());
        BuildTiles();

        _marking.Changed += OnMarkingChanged;
        _marking.Notice += message => _dock.ShowNotice(message, true);

        _capture.SlicesAvailable += OnSlices;
        _capture.DeviceChanged += OnDeviceChanged;
        _capture.PartRolled += OnPartRolled;

        _timer.Tick += (_, _) => Tick();

        // The close button minimises instead of quitting: section 08 says the
        // window close is not an exit while recording.
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => _toast.HideNow();

        SourceInitialized += (_, _) => StartRecording(deviceNumber);
    }

    // ----------------------------------------------------------------- layout

    private UIElement BuildBody()
    {
        var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stats.Children.Add(Stat("Marks", _markCount));
        stats.Children.Add(Stat("Speaking now", _speakingNow));
        stats.Children.Add(Stat("Input", _inputName));
        stats.Children.Add(Stat("Written to disk", _diskWritten));
        stats.Children.Add(_partLabel);
        stats.Children.Add(_droppedLabel);
        stats.Margin = new Thickness(24, 0, 0, 0);

        var undo = Ui.MakeButton("↺ Undo", "Ctrl+Z", "GhostButton", (_, _) =>
        {
            if (!_marking.Undo()) _dock.ShowNotice("Nothing left to undo", false);
        });
        undo.Margin = new Thickness(0, 0, 10, 0);
        _pauseButton.Margin = new Thickness(0, 0, 10, 0);

        var stop = Ui.MakeButton("■ Stop", "Esc", "DangerButton", (_, _) =>
        {
            if (_awaitingStopConfirm) ConfirmStop();
            else BeginStopConfirm();
        });

        var clockGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        clockGroup.Children.Add(_recDot);
        clockGroup.Children.Add(_clock);

        var header = Ui.Columns(2, clockGroup, stats, Ui.Filler(), undo, _pauseButton, stop);
        header.Margin = new Thickness(20, 16, 20, 12);

        var headerRule = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xE9, 0xE9, 0xED)),
        };

        var minimapRow = Ui.Columns(1,
            Fixed(Ui.Section("Minimap"), 64),
            Ui.Well(_minimap, new Thickness(0), 6),
            PadLeft(_minimapClock, 10));
        minimapRow.Margin = new Thickness(20, 8, 20, 0);
        if (minimapRow.Children[1] is FrameworkElement lane) lane.Height = 38;

        var addSpeaker = Ui.MakeButton("＋ Add speaker", "Ctrl+N", "LinkButton", (_, _) => ShowAddStrip());

        var speakersHeader = Ui.Columns(2, PadRight(_speakersHeading, 12), _rosterHint, Ui.Filler(), addSpeaker);
        speakersHeader.Margin = new Thickness(20, 16, 20, 0);

        _tileGrid.Margin = new Thickness(20, 10, 20, 10);

        var gridScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tileGrid,
        };

        var top = new StackPanel();
        top.Children.Add(_confirmBanner);
        top.Children.Add(_noticeBanner);
        top.Children.Add(header);
        top.Children.Add(headerRule);
        top.Children.Add(WavePanel());
        top.Children.Add(minimapRow);
        top.Children.Add(speakersHeader);
        top.Children.Add(_addStrip);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_dockHost, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(_dockHost);
        body.Children.Add(gridScroller);
        return body;
    }

    private UIElement WavePanel()
    {
        _waveWell.Margin = new Thickness(20, 14, 20, 0);
        return _waveWell;
    }

    private static FrameworkElement Fixed(FrameworkElement element, double width)
    {
        element.Width = width;
        return element;
    }

    private static FrameworkElement PadLeft(FrameworkElement element, double left)
    {
        element.Margin = new Thickness(left, 0, 0, 0);
        return element;
    }

    private static FrameworkElement PadRight(FrameworkElement element, double right)
    {
        element.Margin = new Thickness(0, 0, right, 0);
        return element;
    }

    private static TextBlock StatValue(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontFamily = Ui.UiFont,
        Foreground = Palette.TextBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 220,
    };

    private static UIElement Stat(string label, TextBlock value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
        stack.Children.Add(Ui.Text(label, 11, Palette.TextMutedBrush));
        stack.Children.Add(value);
        return stack;
    }

    private static TextBox InlineField(string placeholder) => new()
    {
        FontSize = 14,
        MinWidth = 160,
        Foreground = Palette.TextBrush,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        Tag = placeholder,
    };

    private Border BuildAddStrip()
    {
        var confirm = Ui.MakeButton("Add", "Enter", "ChipButtonAccent", (_, _) => CommitAddSpeaker());
        var cancel = Ui.MakeButton("Cancel", "Esc", "ChipButton", (_, _) => HideAddStrip());
        cancel.Margin = new Thickness(8, 0, 0, 0);

        var row = Ui.Columns(2,
            Ui.Text("New speaker", 12.5, Palette.AccentTextBrush),
            PadLeft(FieldCard("Name", _addName), 14),
            PadLeft(FieldCard("Role", _addRole), 10),
            confirm,
            cancel);

        var strip = Ui.Card(row, new Thickness(14, 10, 14, 10), Palette.AccentEdgeBrush);
        strip.Margin = new Thickness(20, 10, 20, 0);
        strip.Visibility = Visibility.Collapsed;
        return strip;
    }

    private static FrameworkElement FieldCard(string label, TextBox field)
    {
        return Ui.Well(Ui.Vertical(0,
            Ui.Text(label, 10, Palette.TextMutedBrush),
            field), new Thickness(10, 6, 10, 6), 6);
    }

    private void BuildTiles()
    {
        var count = _session.Speakers.Count;
        var density = SpeakerTile.DensityFor(count);

        _tileGrid.Columns = SpeakerTile.ColumnsFor(count);
        _tileGrid.Children.Clear();
        _tiles.Clear();

        foreach (var speaker in _session.Speakers)
        {
            var tile = new SpeakerTile(speaker, density) { Margin = new Thickness(5) };
            tile.Tapped += slot => ToggleSpeaker(slot, viaHotkey: false);
            _tileGrid.Children.Add(tile);
            _tiles[speaker.SlotIndex] = tile;
        }

        if (count < 12)
        {
            var add = new Border
            {
                Height = SpeakerTile.HeightFor(density),
                CornerRadius = new CornerRadius(8),
                BorderBrush = Palette.AccentEdgeBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(5),
                Cursor = Cursors.Hand,
                Child = Ui.Text("＋ Add", 14, Palette.AccentTextBrush),
            };
            if (add.Child is FrameworkElement label)
            {
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.VerticalAlignment = VerticalAlignment.Center;
            }
            add.MouseLeftButtonUp += (_, _) => ShowAddStrip();
            _tileGrid.Children.Add(add);
        }

        _speakersHeading.Text = ("Speakers · " + count + " of 12").ToUpperInvariant();
        _rosterHint.Text = count >= 12
            ? "roster is full — mark absent participants instead of adding a thirteenth"
            : "keyboard is the primary control; clicking a tile is the same action. Opening a new speaker closes the previous one.";

        _toast.SetRoster(_session.Speakers);
    }

    // ------------------------------------------------------------- recording

    private void StartRecording(int deviceNumber)
    {
        try
        {
            _capture.Start(deviceNumber, _session.SessionFolder, _session.AudioBaseName,
                _session.Options.SplitMinutes);
            _session.AudioParts = _capture.Parts.ToList();
            if (_session.AudioParts.Count > 0) _session.AudioFileName = _session.AudioParts[0].FileName;
        }
        catch (Exception ex)
        {
            ShowNotice("Could not open the input device: " + ex.Message +
                       " — nothing is being recorded. Stop and try another device.");
        }

        _session.AudioFormatDescription = _capture.FormatDescription;
        if (!string.IsNullOrEmpty(_capture.DeviceName)) _session.InputDeviceName = _capture.DeviceName;
        _inputName.Text = _session.InputDeviceName;
        SessionStore.Save(_session);

        // Sleep and display-off are inhibited for the session duration.
        PowerKeepAwake.Begin();

        _hotkeys.Attach(this);
        _hotkeys.Pressed += OnGlobalHotkey;
        _hotkeys.Register(_session.Speakers.Select(s => s.Key));
        if (_hotkeys.Failed.Count > 0)
        {
            // A silently unregistered hotkey looks identical to a missed
            // press, so it is named rather than swallowed (section 11).
            ShowNotice("Windows refused these global hotkeys — another app already holds them: " +
                       string.Join(", ", _hotkeys.Failed.Select(k => k.GlobalLabel)) +
                       ". The plain " + string.Join(", ", _hotkeys.Failed.Select(k => k.Label)) +
                       " keys still mark while this window has focus.");
        }

        _timer.Start();
        Tick();
    }

    private void OnSlices(WaveSlice[] slices)
    {
        // Handed over on the capture thread and drained on the UI timer, so
        // nothing in the UI can stall the encoder.
        foreach (var slice in slices) _slices.Enqueue(slice);
    }

    /// <summary>The recorder rolled to the next MP3; say so and keep the session file honest.</summary>
    private void OnPartRolled(AudioPart part)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _session.AudioParts = _capture.Parts.ToList();
            _dock.ShowNotice("Started " + _session.AudioParts[^1].FileName + " — recording never stopped", false);
            UpdatePartLabel();
            PersistSession(_capture.ElapsedSeconds);
        });
    }

    private void OnDeviceChanged(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _session.Notes.Add(message);
            _session.InputDeviceName = _capture.DeviceName;
            _inputName.Text = _capture.DeviceName;
            ShowNotice(message);
        });
    }

    private void ShowNotice(string message)
    {
        _noticeText.Text = message;
        _noticeBanner.Visibility = Visibility.Visible;
    }

    private void Tick()
    {
        var elapsed = _capture.ElapsedSeconds;
        _marking.CurrentFileSeconds = elapsed;

        while (_slices.TryDequeue(out var slice))
        {
            _waveform.Push(slice);
        }

        _clock.Text = Ui.Clock(elapsed);
        _markCount.Text = _marking.Marks.Count.ToString();
        _diskWritten.Text = _capture.WrittenMegabytes.ToString("0.0") + " MB";
        _droppedLabel.Text = _capture.DroppedBuffers > 0
            ? "⚠ " + _capture.DroppedBuffers + " dropped buffers"
            : "";
        UpdatePartLabel();

        if (_marking.ActiveSlot is int slot)
        {
            var speaker = _session.SpeakerForSlot(slot);
            _speakingNow.Text = speaker?.Name ?? "unknown";
            _speakingNow.Foreground = Palette.BrushForSlot(slot);
        }
        else
        {
            _speakingNow.Text = _capture.IsPaused ? "paused" : "nobody";
            _speakingNow.Foreground = Palette.TextMutedBrush;
        }

        _waveform.CurrentSeconds = elapsed;
        _waveform.IsPaused = _capture.IsPaused;
        _waveform.SetBoundaries(BuildBoundaries(elapsed));
        _waveform.InvalidateVisual();

        _minimap.TotalSeconds = Math.Max(1, elapsed);
        _minimap.ViewportStart = Math.Max(0, elapsed - _waveform.WindowSeconds);
        _minimap.ViewportEnd = elapsed;
        _minimap.SetMarks(_marking.Chronological());
        _minimapClock.Text = Ui.Clock(elapsed) + " / whole session";
        _dock.UpdateLive(elapsed);

        UpdateTiles(elapsed);

        // The rec dot pulses while recording and holds steady while paused,
        // so a glance at the corner is enough to tell the two apart.
        _blinkPhase += 0.1;
        _recDot.Fill = _capture.IsPaused ? Palette.TextMutedBrush : Palette.RecBrush;
        _recDot.Opacity = _capture.IsPaused ? 0.5 : 0.75 + 0.25 * Math.Sin(_blinkPhase * 2.2);

        if (elapsed - _lastSavedAt > 10)
        {
            _lastSavedAt = elapsed;
            PersistSession(elapsed);
        }
    }

    private IReadOnlyList<WaveformBoundary> BuildBoundaries(double elapsed)
    {
        var from = elapsed - _waveform.WindowSeconds;
        var boundaries = new List<WaveformBoundary>();

        foreach (var mark in _marking.Marks)
        {
            if (mark.StartSeconds < from || mark.StartSeconds > elapsed) continue;
            var speaker = _session.SpeakerForSlot(mark.SpeakerSlot);
            boundaries.Add(new WaveformBoundary(mark.StartSeconds, Palette.ForSlot(mark.SpeakerSlot),
                speaker?.Initial ?? "?", false));
        }

        foreach (var open in _marking.Open)
        {
            if (open.StartSeconds < from) continue;
            var speaker = _session.SpeakerForSlot(open.SpeakerSlot);
            boundaries.Add(new WaveformBoundary(open.StartSeconds, Palette.ForSlot(open.SpeakerSlot),
                speaker?.Initial ?? "?", true));
        }

        return boundaries;
    }

    private void UpdateTiles(double elapsed)
    {
        var marked = _session.Speakers.Sum(s => _marking.TalkTimeFor(s.SlotIndex, elapsed));
        foreach (var (slot, tile) in _tiles)
        {
            var isOpen = _marking.IsOpen(slot);
            var talk = _marking.TalkTimeFor(slot, elapsed);
            var markStart = _marking.OpenStartFor(slot);
            var openFor = isOpen ? elapsed - markStart : 0;
            tile.Update(isOpen, openFor, talk, marked > 0 ? talk / marked : 0, markStart);
        }
    }

    /// <summary>Which MP3 is being written, when the operator asked for a split.</summary>
    private void UpdatePartLabel()
    {
        if (_session.Options.SplitMinutes <= 0)
        {
            _partLabel.Text = "";
            return;
        }

        var count = Math.Max(1, _capture.Parts.Count);
        _partLabel.Text = "file " + count + " · splits every " + _session.Options.SplitMinutes + " min";
        _partLabel.Margin = new Thickness(0, 0, 20, 0);
    }

    private void PersistSession(double elapsed)
    {
        try
        {
            _session.AudioDurationSeconds = elapsed;
            _session.Marks = _marking.Marks.Select(m => m.Clone()).ToList();
            _session.DroppedBufferCount = _capture.DroppedBuffers;
            SessionStore.Save(_session);
        }
        catch (Exception)
        {
            // The journal is the durable record; session.json is a summary.
        }
    }

    private void OnMarkingChanged()
    {
        _dock.Refresh();
        _markCount.Text = _marking.Marks.Count.ToString();
    }

    private void OnDockLayoutChanged()
    {
        // Expanding takes space from the grid's spare height and from the
        // live waveform (132px → 72px), never from the timecode, the tiles
        // or the Stop button.
        _waveWell.Height = _dock.IsExpanded ? 72 : 132;
        _dockHost.MaxHeight = _dock.IsExpanded ? 380 : 232;
    }

    // -------------------------------------------------------------- marking

    private void ToggleSpeaker(int slot, bool viaHotkey)
    {
        if (_awaitingStopConfirm || _stopping) return;

        var result = _marking.Toggle(slot, _capture.ElapsedSeconds);
        if (viaHotkey && !IsActive)
        {
            _toast.ShowMark(_session, result, _capture.ElapsedSeconds);
        }
    }

    /// <summary>
    /// The arrows trim the mark that is open right now, 0.1 s a press — the
    /// repair for a key pressed a beat late, made without leaving the grid.
    /// The waveform flag and the speaker tile both read from the same start,
    /// so they move with it.
    ///
    /// Once a row is selected in the expanded dock, the arrows belong to that
    /// row instead; that is the deliberate editing mode, and taking it over
    /// would make the dock's own start/end fields unreachable.
    /// </summary>
    private void NudgeMark(int direction, bool coarse)
    {
        if (_dock.IsExpanded && _dock.SelectedMarkId is not null)
        {
            _dock.Nudge(direction * (coarse
                ? MarkingEngine.NudgeStepSeconds
                : MarkingEngine.FineNudgeStepSeconds));
            return;
        }

        var step = direction * (coarse
            ? MarkingEngine.NudgeStepSeconds
            : MarkingEngine.FineNudgeStepSeconds);

        if (_marking.NudgeOpenStart(step)) return;

        _dock.ShowNotice(_marking.ActiveSlot is null
            ? "Nothing is open — press a speaker's key first, then ← → to trim its start"
            : "That start cannot move any further", false);
    }

    private void OnGlobalHotkey(MarkKey key)
    {
        var speaker = _session.Speakers.FirstOrDefault(s => s.Key == key);
        if (speaker is null) return;
        ToggleSpeaker(speaker.SlotIndex, viaHotkey: true);
    }

    // ------------------------------------------------------------- keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_addStrip.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter) { CommitAddSpeaker(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideAddStrip(); e.Handled = true; }
            return;
        }

        if (_awaitingStopConfirm)
        {
            if (e.Key == Key.Enter) { ConfirmStop(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelStopConfirm(); e.Handled = true; }
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    if (!_marking.Undo()) _dock.ShowNotice("Nothing left to undo", false);
                    e.Handled = true;
                    return;
                case Key.Y:
                    if (!_marking.Redo()) _dock.ShowNotice("Nothing to redo", false);
                    e.Handled = true;
                    return;
                case Key.P:
                    TogglePause();
                    e.Handled = true;
                    return;
                case Key.E:
                    _dock.Toggle();
                    e.Handled = true;
                    return;
                case Key.N:
                    ShowAddStrip();
                    e.Handled = true;
                    return;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                BeginStopConfirm();
                e.Handled = true;
                return;
            case Key.Space:
                _marking.CloseAll(_capture.ElapsedSeconds);
                e.Handled = true;
                return;
            case Key.Up:
                _dock.MoveSelection(-1);
                e.Handled = true;
                return;
            case Key.Down:
                _dock.MoveSelection(1);
                e.Handled = true;
                return;
            case Key.Enter:
                _dock.OpenSelected();
                e.Handled = true;
                return;
            case Key.Tab:
                _dock.ToggleBoundary();
                e.Handled = true;
                return;
            case Key.Delete:
                _dock.DeleteSelected();
                e.Handled = true;
                return;
            case Key.Left:
                NudgeMark(-1, shift);
                e.Handled = true;
                return;
            case Key.Right:
                NudgeMark(1, shift);
                e.Handled = true;
                return;
        }

        // A global hotkey Windows refused never reaches the message loop as a
        // plain digit — Alt+1 arrives as Key.System with the digit in
        // SystemKey — so unwrap it here and let the press mark anyway.
        var digitKey = e.Key == Key.System ? e.SystemKey : e.Key;

        if (KeyMap.ToMarkKey(digitKey, shift) is { } markKey)
        {
            var speaker = _session.Speakers.FirstOrDefault(s => s.Key == markKey);
            if (speaker is not null)
            {
                ToggleSpeaker(speaker.SlotIndex, viaHotkey: false);
                e.Handled = true;
            }
        }
    }

    // ---------------------------------------------------------- add speaker

    private void ShowAddStrip()
    {
        if (_session.Speakers.Count >= 12)
        {
            _dock.ShowNotice("Twelve slots are all taken — mark an absent participant instead", false);
            return;
        }

        _addName.Text = "";
        _addRole.Text = "";
        _addStrip.Visibility = Visibility.Visible;
        _addName.Focus();
    }

    private void HideAddStrip()
    {
        _addStrip.Visibility = Visibility.Collapsed;
        Focus();
    }

    /// <summary>
    /// Section 09: the roster grows and hotkeys renumber only by appending,
    /// and the new speaker takes the lowest free key automatically so marking
    /// continues without a dialog.
    /// </summary>
    private void CommitAddSpeaker()
    {
        var name = _addName.Text.Trim();
        if (name.Length == 0)
        {
            HideAddStrip();
            return;
        }

        var slot = _session.Speakers.Count;
        var key = MarkKey.ForSlot(slot);
        for (var candidate = 0; candidate < 12; candidate++)
        {
            var option = MarkKey.ForSlot(candidate);
            if (_session.Speakers.All(s => s.Key != option))
            {
                key = option;
                break;
            }
        }

        _session.Speakers.Add(new Speaker
        {
            SlotIndex = slot,
            Name = name,
            Role = _addRole.Text.Trim(),
            Key = key,
        });

        _hotkeys.UnregisterAll();
        _hotkeys.Register(_session.Speakers.Select(s => s.Key));

        HideAddStrip();
        BuildTiles();
        SessionStore.Save(_session);
    }

    // -------------------------------------------------------------- pause

    private void TogglePause()
    {
        if (_capture.IsPaused)
        {
            _capture.Resume();
            if (_pausedAt is DateTime pausedAt)
            {
                _session.PausedTotalSeconds += (DateTime.Now - pausedAt).TotalSeconds;
            }
            _pausedAt = null;
            _pauseButton.Content = PauseContent("⏸ Pause");
        }
        else
        {
            // Pause closes the open mark the same way Stop does and drops the
            // paused span out of the file entirely, so the timeline the
            // operator sees after resuming is continuous — because the file is.
            _marking.AutoCloseAt(_capture.ElapsedSeconds);
            _capture.Pause();
            _pausedAt = DateTime.Now;
            _session.PauseCount++;
            _pauseButton.Content = PauseContent("▶ Resume");
        }
        PersistSession(_capture.ElapsedSeconds);
    }

    private static UIElement PauseContent(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Text(label, 13.5, Palette.TextBodyBrush));
        row.Children.Add(Ui.Mono("Ctrl+P", 11, Palette.TextMutedBrush));
        ((FrameworkElement)row.Children[1]).Margin = new Thickness(7, 0, 0, 0);
        return row;
    }

    // --------------------------------------------------------------- stop

    private void BeginStopConfirm()
    {
        _awaitingStopConfirm = true;
        _confirmText.Text = "Stop recording? " + Ui.Clock(_capture.ElapsedSeconds) +
                            " captured · Enter to confirm, Esc to keep going";
        _confirmBanner.Visibility = Visibility.Visible;
    }

    private void CancelStopConfirm()
    {
        _awaitingStopConfirm = false;
        _confirmBanner.Visibility = Visibility.Collapsed;
    }

    private void ConfirmStop()
    {
        if (_stopping) return;
        _stopping = true;

        _timer.Stop();
        _capture.SlicesAvailable -= OnSlices;
        _capture.DeviceChanged -= OnDeviceChanged;
        _capture.PartRolled -= OnPartRolled;
        _marking.Changed -= OnMarkingChanged;

        // Any mark still open at Stop is closed at the stop timestamp and
        // flagged in the Markdown with auto-closed (section 08).
        _marking.AutoCloseAt(_capture.ElapsedSeconds);

        _session.AudioDurationSeconds = _capture.ElapsedSeconds;
        _session.AudioParts = _capture.Parts.ToList();
        if (_session.AudioParts.Count > 0) _session.AudioFileName = _session.AudioParts[0].FileName;
        _session.Marks = _marking.Marks.Select(m => m.Clone()).OrderBy(m => m.StartSeconds).ToList();
        _session.Gaps = _marking.ComputeGaps(_session.AudioDurationSeconds).ToList();
        _session.DroppedBufferCount = _capture.DroppedBuffers;
        _session.EndedAt = DateTimeOffset.Now;

        _capture.Stop();
        PowerKeepAwake.End();
        _hotkeys.Dispose();
        _journal.Dispose();
        _toast.HideNow();

        SessionStore.Save(_session);

        var export = new ExportWindow(_session, alreadyWritten: false);
        export.Show();

        Closing -= OnClosing;
        try { _toast.Close(); } catch (Exception) { }
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_stopping) return;

        // Stop is the only exit, and it is guarded. Closing the window
        // minimises instead; global hotkeys and the toast keep working.
        e.Cancel = true;
        WindowState = WindowState.Minimized;
        if (!_awaitingStopConfirm) ShowNotice("Still recording in the background — Alt+1…0 keep marking. Press Esc here to stop.");
    }
}
