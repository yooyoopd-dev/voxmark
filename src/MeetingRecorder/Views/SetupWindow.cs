using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingRecorder.Controls;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// S2 · Setup — design guide section 07. Two panes: the meeting and its
/// input on the left, the roster and recording options on the right, with
/// the session folder and Start recording along the bottom.
///
/// The level check is deliberately prominent rather than skippable: with ten
/// people around a table the room mic is the weakest link in the whole
/// chain, and the guide calls that out explicitly.
/// </summary>
public sealed class SetupWindow : ShellWindow
{
    private readonly List<Speaker> _roster = new();
    private readonly List<(int Id, string Name)> _devices = new();
    private readonly InputLevelMeter _meter = new();
    private readonly SessionOptions _options = new();

    private readonly TextBox _title;
    private readonly TextBox _room;
    private readonly TextBox _dateTime;
    private readonly TextBlock _dateHint;
    private readonly Dropdown _device;
    private readonly Border _levelFill;
    private readonly Border _levelTrack;
    private readonly TextBlock _levelStatus;
    private readonly TextBlock _diskText;
    private readonly TextBlock _sessionsRootText;
    private readonly TextBox _log;
    private readonly Button _copyLogButton;
    private readonly StackPanel _rosterPanel = new();
    private readonly WrapPanel _presetChips = new();
    private readonly TextBlock _rosterHeading;
    private readonly TextBlock _folderPreview;
    private readonly CheckBox _overlapToggle;
    private readonly Dropdown _offset;
    private readonly Dropdown _split;
#if !VOXMARK_LITE
    private readonly CheckBox _transcribeToggle;
    private readonly TextBlock _modelName;
    private readonly TextBlock _transcribeStatus;
#endif

    private List<Preset> _presets = new();
    private RosterRow? _capturingRow;
    private RosterRow? _dragSource;
    private double _peak;
    private string _planId = "";
    private readonly DispatcherTimer _meterWatchdog = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime _lastSignalAt = DateTime.MinValue;
    private bool _retriedMeter;

    /// <summary>
    /// A blank setup, or one seeded from a meeting the operator saved
    /// earlier — same screen either way, so a plan is just a starting point
    /// that can still be edited before Start.
    /// </summary>
    public SetupWindow(MeetingPlan? plan = null)
        : base(plan is null ? "New meeting — setup" : "Setup — " + plan.Title, 1320, 820)
    {
        MinWidth = 1060;
        MinHeight = 700;

        _title = Field(plan?.Title ?? "Weekly Product Review", 15);
        _room = Field(plan?.Room ?? "", 14);
        _dateTime = Field((plan?.ScheduledAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm"), 14);
        _dateTime.FontFamily = Ui.MonoFont;
        _dateHint = Ui.Text("", 10, Palette.RecBrush);
        _dateHint.Visibility = Visibility.Collapsed;

        _device = new Dropdown { MinHeight = 34 };
        _device.SelectionChanged += _ => OnDeviceChanged();

        _levelFill = new Border
        {
            Height = 8,
            Width = 0,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush(Palette.Good, Palette.Warn, 0),
        };
        _levelTrack = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Palette.WellBrush,
            Child = _levelFill,
        };

        _levelStatus = Ui.Text("Say something to test the level", 11.5, Palette.TextMutedBrush);
        _levelStatus.HorizontalAlignment = HorizontalAlignment.Center;

        _diskText = Ui.Mono("—", 12.5, Palette.TextBodyBrush);
        _rosterHeading = Ui.Section("Roster · 0");
        _folderPreview = Ui.Mono("—", 12, Palette.TextBodyBrush);

        _sessionsRootText = Ui.Mono("—", 12, Palette.TextBodyBrush);
        _sessionsRootText.MaxWidth = 220;
        _sessionsRootText.TextTrimming = TextTrimming.CharacterEllipsis;

        _log = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = Ui.MonoFont,
            FontSize = 11.5,
            Foreground = Palette.TextMutedBrush,
            Height = 90,
            Text = "No issues yet",
        };
        _copyLogButton = Ui.MakeButton("Copy", null, "GhostButton", (_, _) =>
        {
            if (_log.Text.Length > 0) Clipboard.SetText(_log.Text);
        });

        _overlapToggle = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("ToggleSwitch") is Style toggle) _overlapToggle.Style = toggle;
        _overlapToggle.Checked += (_, _) => _options.AllowOverlappingMarks = true;
        _overlapToggle.Unchecked += (_, _) => _options.AllowOverlappingMarks = false;

        _offset = new Dropdown("ChipButton") { MinHeight = 26 };
        _offset.SetItems(new (string, object)[]
        {
            ("−0.0 s", 0.0), ("−0.4 s", 0.4), ("−0.8 s", 0.8), ("−1.2 s", 1.2), ("−1.6 s", 1.6),
        });
        _offset.SelectionChanged += value =>
        {
            if (value is double seconds) _options.MarkStartOffsetSeconds = seconds;
        };

        _split = new Dropdown("ChipButton") { MinHeight = 26 };
        _split.SetItems(new (string, object)[]
        {
            ("One file", 0),
            ("Every 1 min", 1), ("Every 2 min", 2), ("Every 5 min", 5),
            ("Every 10 min", 10), ("Every 15 min", 15),
            ("Every 30 min", 30), ("Every 60 min", 60),
        });
        _split.SelectionChanged += value =>
        {
            if (value is int minutes) _options.SplitMinutes = minutes;
        };

#if !VOXMARK_LITE
        // Remembered across meetings: choosing a model is setup, and being
        // asked for it again before every meeting would be a chore rather
        // than a decision.
        WhisperRuntime.EnsureModelsFolder();
        var transcription = TranscriptionSettingsStore.Load();
        _options.WhisperModelPath = transcription.ModelPath;
        _options.TranscriptionLanguage = transcription.Language;
        _transcriptionPreferred = transcription.Enabled;

        _modelName = Ui.Mono("—", 12, Palette.TextBodyBrush);
        _modelName.MaxWidth = 220;
        _modelName.TextTrimming = TextTrimming.CharacterEllipsis;
        _transcribeStatus = Ui.Wrap("", 11.5, Palette.TextMutedBrush);

        _transcribeToggle = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("ToggleSwitch") is Style transcribeStyle) _transcribeToggle.Style = transcribeStyle;
        _transcribeToggle.Checked += (_, _) => SetTranscription(true);
        _transcribeToggle.Unchecked += (_, _) => SetTranscription(false);
#endif

        if (plan is not null)
        {
            _planId = plan.Id;
            _options.AllowOverlappingMarks = plan.Options.AllowOverlappingMarks;
            _options.MarkStartOffsetSeconds = plan.Options.MarkStartOffsetSeconds;
            _options.Mp3BitrateKbps = plan.Options.Mp3BitrateKbps;
            _options.SplitMinutes = plan.Options.SplitMinutes;
            // Whether to transcribe is a decision about *this meeting*, so a
            // plan carries it. Which model file to use is a fact about *this
            // machine*, so it stays with the machine — a plan written on
            // another PC would otherwise point at a path that does not exist.
#if !VOXMARK_LITE
            _transcriptionPreferred = plan.Options.TranscriptionEnabled;
#endif
            _roster.AddRange(plan.Speakers.Select(sp => sp.Clone()));
        }

        _overlapToggle.IsChecked = _options.AllowOverlappingMarks;
        _offset.Select(_options.MarkStartOffsetSeconds);
        _offset.DisplayText = "−" + _options.MarkStartOffsetSeconds.ToString("0.0") + " s";
        _split.Select(_options.SplitMinutes);
        _split.DisplayText = _options.SplitMinutes > 0
            ? "Every " + _options.SplitMinutes + " min"
            : "One file";

        SetBody(BuildBody());

        LoadDevices();
        LoadPresets();
        SeedRoster();
        UpdateFolderPreview();
#if !VOXMARK_LITE
        RefreshTranscriptionState();
#endif

        _meter.LevelChanged += OnLevel;
        _title.TextChanged += (_, _) => UpdateFolderPreview();

        Loaded += (_, _) =>
        {
            StartMeter();
            _title.Focus();
            _title.SelectAll();
        };
        _meterWatchdog.Tick += (_, _) => CheckMeter();
        Closed += (_, _) =>
        {
            _meterWatchdog.Stop();
            _meter.Dispose();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ----------------------------------------------------------------- layout

    private UIElement BuildBody()
    {
        var left = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20),
            Content = BuildLeftPane(),
        };

        var right = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20),
            Content = BuildRightPane(),
        };

        var divider = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xE9, 0xE9, 0xED)),
        };

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(divider);
        columns.Children.Add(right);

        var root = new DockPanel { LastChildFill = true };
        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(columns);
        return root;
    }

    private UIElement BuildLeftPane()
    {
        var titleCard = Ui.Card(Ui.Vertical(2,
            Ui.Text("Title", 10, Palette.TextMutedBrush),
            _title), new Thickness(13, 11, 13, 11));

        var dateCard = Ui.Card(Ui.Vertical(2,
            Ui.Columns(0,
                Ui.Text("Date & time", 10, Palette.TextMutedBrush),
                _dateHint),
            _dateTime), new Thickness(13, 11, 13, 11));
        _dateTime.TextChanged += (_, _) => ValidateDate();

        var roomCard = Ui.Card(Ui.Vertical(2,
            Ui.Text("Room", 10, Palette.TextMutedBrush),
            _room), new Thickness(13, 11, 13, 11));

        var dateRow = new Grid();
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dateCard.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(dateCard, 0);
        Grid.SetColumn(roomCard, 1);
        dateRow.Children.Add(dateCard);
        dateRow.Children.Add(roomCard);

        var presetHeader = Ui.Columns(0,
            Ui.Section("Load a preset"),
            Ui.MakeButton("Manage", null, "LinkButton", (_, _) => ToggleManagePresets()));

        var deviceRow = Ui.Columns(0,
            Ui.Text("Input device", 14, Palette.TextBrush),
            _device);
        _device.MinWidth = 240;

        var scale = new Grid();
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scale.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lowDb = Ui.Mono("−48 dB", 11, Palette.TextMutedBrush);
        var highDb = Ui.Mono("0 dB", 11, Palette.TextMutedBrush);
        Grid.SetColumn(lowDb, 0);
        Grid.SetColumn(_levelStatus, 1);
        Grid.SetColumn(highDb, 2);
        scale.Children.Add(lowDb);
        scale.Children.Add(_levelStatus);
        scale.Children.Add(highDb);
        scale.Margin = new Thickness(0, 6, 0, 0);

        var diskRow = Ui.Columns(1,
            Ui.Text("Free disk space", 12.5, Palette.TextBodyBrush),
            Ui.Filler(),
            _diskText);
        diskRow.Margin = new Thickness(0, 10, 0, 0);

        var browseSave = Ui.MakeButton("Browse…", null, "ChipButton", (_, _) => BrowseForSaveFolder());
        browseSave.Margin = new Thickness(8, 0, 0, 0);
        var resetSave = Ui.MakeButton("Reset", null, "LinkButton", (_, _) => ResetSaveFolder());
        resetSave.Margin = new Thickness(6, 0, 0, 0);

        var saveToRow = Ui.Columns(1,
            Ui.Text("Save recordings to", 12.5, Palette.TextBodyBrush),
            Ui.Filler(),
            _sessionsRootText,
            browseSave,
            resetSave);
        saveToRow.Margin = new Thickness(0, 10, 0, 0);

        var saveToNote = Ui.Wrap(
            "Sessions already saved under a previous location won't move or show up in the " +
            "Library after switching — this redirects new meetings, it doesn't migrate old ones.",
            11, Palette.TextMutedBrush);
        saveToNote.Margin = new Thickness(0, 4, 0, 0);

        var inputCard = Ui.Card(Ui.Vertical(10,
            deviceRow,
            _levelTrack,
            scale,
            Ui.Rule(),
            saveToRow,
            saveToNote,
            diskRow), new Thickness(13));

        var logHeader = Ui.Columns(1,
            Ui.Text("Diagnostics from a failed save-folder creation appear here, copyable.",
                11.5, Palette.TextMutedBrush),
            Ui.Filler(),
            _copyLogButton);

        var logCard = Ui.Card(Ui.Vertical(8,
            logHeader,
            Ui.Well(_log, new Thickness(8), 6)), new Thickness(13));

        return Ui.Vertical(0,
            WithMargin(Ui.Section("Meeting"), 0, 0, 0, 10),
            WithMargin(titleCard, 0, 0, 0, 8),
            WithMargin(dateRow, 0, 0, 0, 20),
            WithMargin(presetHeader, 0, 0, 0, 10),
            WithMargin(_presetChips, 0, 0, 0, 20),
            WithMargin(Ui.Section("Input check"), 0, 0, 0, 10),
            WithMargin(inputCard, 0, 0, 0, 20),
            WithMargin(Ui.Section("Log"), 0, 0, 0, 10),
            logCard);
    }

    private UIElement BuildRightPane()
    {
        var header = Ui.Columns(0,
            _rosterHeading,
            Ui.Text("drag to reorder · click a key cell to reassign", 12, Palette.TextMutedBrush));
        header.Margin = new Thickness(0, 0, 0, 10);

        _rosterPanel.AllowDrop = true;
        _rosterPanel.Drop += OnRosterDrop;
        _rosterPanel.DragOver += (_, e) => e.Effects = DragDropEffects.Move;

        var add = Ui.MakeButton("＋ Add speaker", "Ctrl+N", "OutlineAccentButton", (_, _) => AddSpeaker());
        add.MinHeight = 42;
        add.HorizontalContentAlignment = HorizontalAlignment.Center;

        var format = Ui.Columns(1,
            Ui.Text("Format", 13.5, Palette.TextBrush),
            Ui.Filler(),
            Ui.Mono("MP3 · 128 kbps · 44.1 kHz mono", 12.5, Palette.AccentTextBrush));

        var overlap = Ui.Columns(1,
            Ui.Text("Allow overlapping marks", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _overlapToggle);

        var offsetRow = Ui.Columns(1,
            Ui.Text("Mark start offset", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _offset);

        var splitRow = Ui.Columns(1,
            Ui.Text("Split recording", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _split);

        var optionRows = new List<UIElement>
        {
            Pad(format, 11),
            Ui.Rule(),
            Pad(overlap, 11),
            Ui.Rule(),
            Pad(offsetRow, 11),
            Ui.Rule(),
            Pad(splitRow, 11),
        };

#if !VOXMARK_LITE
        var browse = Ui.MakeButton("Browse", null, "ChipButton", (_, _) => BrowseForModel());
        browse.Margin = new Thickness(8, 0, 0, 0);

        optionRows.Add(Ui.Rule());
        optionRows.Add(Pad(Ui.Columns(1,
            Ui.Text("Live transcription", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _transcribeToggle), 11));
        optionRows.Add(Pad(Ui.Columns(1,
            Ui.Text("Speech model", 12.5, Palette.TextDimBrush),
            Ui.Filler(),
            _modelName,
            browse), 4));
        optionRows.Add(Pad(_transcribeStatus, 4));
#endif

        var optionsCard = Ui.Card(Ui.Vertical(0, optionRows.ToArray()), new Thickness(13, 2, 13, 2));

        var offsetNote = Ui.Wrap(
            "A human presses the key after the speaker has already begun, so every mark start is shifted back " +
            "by the offset automatically; the raw press time is kept in the log so it can be re-tuned later. " +
            "Splitting rolls to a new MP3 on the chosen interval and writes a matching Markdown for each one — " +
            "timestamps keep counting from the first file, so they mean the same thing in every chunk."
#if !VOXMARK_LITE
            + " Live transcription recognises speech on this PC and maps the words onto your speaker " +
            "marks in the exported Markdown. The model is a file you supply — VoxMark never downloads " +
            "one and makes no network calls."
#endif
            ,
            11.5, Palette.TextMutedBrush);
        offsetNote.Margin = new Thickness(2, 9, 2, 0);

        return Ui.Vertical(0,
            header,
            _rosterPanel,
            WithMargin(add, 0, 0, 0, 18),
            WithMargin(Ui.Section("Recording options"), 0, 0, 0, 10),
            optionsCard,
            offsetNote);
    }

    private UIElement BuildFooter()
    {
        var start = Ui.MakeButton("● Start recording", "Ctrl+R", "AccentButton", (_, _) => StartRecording());

        var savePreset = Ui.MakeButton("Save as preset", null, "GhostButton", (_, _) => SavePreset());
        savePreset.MinHeight = 40;
        savePreset.Margin = new Thickness(0, 0, 10, 0);

        var saveSetup = Ui.MakeButton("Save setup", "Ctrl+S", "OutlineAccentButton", (_, _) => SaveSetup());
        saveSetup.MinHeight = 40;
        saveSetup.Margin = new Thickness(0, 0, 10, 0);

        var back = Ui.MakeButton("← Back", null, "GhostButton", (_, _) => GoBack());
        back.MinHeight = 40;
        back.Margin = new Thickness(0, 0, 14, 0);

        // The folder path is the star column and trims with an ellipsis, so a
        // long path eats its own slack instead of pushing the buttons out of
        // the window. The buttons stay Auto and always keep their full width.
        _folderPreview.TextTrimming = TextTrimming.CharacterEllipsis;
        _folderPreview.Margin = new Thickness(10, 0, 20, 0);

        var row = Ui.Columns(2,
            back,
            Ui.Text("Session folder", 12.5, Palette.TextMutedBrush),
            _folderPreview,
            saveSetup,
            savePreset,
            start);
        row.Margin = new Thickness(20, 14, 20, 14);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xE9, 0xE9, 0xED)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Palette.ChromeBrush,
            Child = row,
        };
    }

    private static FrameworkElement WithMargin(FrameworkElement element, double l, double t, double r, double b)
    {
        element.Margin = new Thickness(l, t, r, b);
        return element;
    }

    private static FrameworkElement Pad(FrameworkElement element, double vertical, double horizontal = 0)
    {
        element.Margin = new Thickness(horizontal, vertical, horizontal, vertical);
        return element;
    }

    private static TextBox Field(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Palette.TextBrush,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
    };

    // ------------------------------------------------------------------ roster

    private void SeedRoster()
    {
        // A plan arrives with its roster already in _roster, but nothing has
        // drawn it yet — without this the rows only appeared once something
        // else forced a rebuild, which looked like the plan had lost them.
        if (_roster.Count == 0) AddSpeaker();
        else RebuildRoster();
    }

    private void AddSpeaker(string name = "", string role = "")
    {
        if (_roster.Count >= 12) return;

        var speaker = new Speaker
        {
            SlotIndex = _roster.Count,
            Name = name,
            Role = role,
            Key = LowestFreeKey(),
        };
        _roster.Add(speaker);
        RebuildRoster();

        if (_rosterPanel.Children.Count > 0 &&
            _rosterPanel.Children[^1] is RosterRow row && string.IsNullOrEmpty(name))
        {
            row.FocusName();
        }
    }

    /// <summary>A speaker added late takes the lowest free key, so marking continues without a dialog.</summary>
    private MarkKey LowestFreeKey()
    {
        for (var slot = 0; slot < 12; slot++)
        {
            var candidate = MarkKey.ForSlot(slot);
            if (_roster.All(s => s.Key != candidate)) return candidate;
        }
        return MarkKey.ForSlot(_roster.Count);
    }

    private void RebuildRoster()
    {
        // Slots are assigned by roster order and never re-shuffled afterwards.
        for (var i = 0; i < _roster.Count; i++) _roster[i].SlotIndex = i;

        _rosterPanel.Children.Clear();
        foreach (var speaker in _roster)
        {
            var row = new RosterRow(speaker);
            row.RemoveRequested += RemoveRow;
            row.KeyCaptureRequested += BeginKeyCapture;
            row.DragRequested += BeginDrag;
            row.Changed += UpdateRosterHeading;
            _rosterPanel.Children.Add(row);
        }
        UpdateRosterHeading();
    }

    private void UpdateRosterHeading()
    {
        var present = _roster.Count(s => !s.IsAbsent);
        _rosterHeading.Text = ("Roster · " + present + (present == _roster.Count ? "" : " of " + _roster.Count))
            .ToUpperInvariant();
    }

    private void RemoveRow(RosterRow row)
    {
        _roster.Remove(row.Speaker);
        RebuildRoster();
    }

    // --------------------------------------------------------- key reassignment

    private void BeginKeyCapture(RosterRow row)
    {
        _capturingRow?.SetCapturing(false);
        _capturingRow = row;
        row.SetCapturing(true);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingRow is { } row)
        {
            if (e.Key == Key.Escape)
            {
                row.SetCapturing(false);
                _capturingRow = null;
                e.Handled = true;
                return;
            }

            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (KeyMap.ToMarkKey(e.Key, shift) is { } key)
            {
                // Rejected at keystroke time rather than accepted and warned
                // about later (section 06).
                if (_roster.Any(s => s != row.Speaker && s.Key == key))
                {
                    row.RejectKey();
                }
                else
                {
                    row.Speaker.Key = key;
                    row.SetCapturing(false);
                    _capturingRow = null;
                }
                e.Handled = true;
                return;
            }
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.R)
        {
            StartRecording();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N)
        {
            AddSpeaker();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            SaveSetup();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ drag reorder

    private void BeginDrag(RosterRow row)
    {
        _dragSource = row;
        try
        {
            DragDrop.DoDragDrop(row, row, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // A drag that the shell refuses is not worth failing over.
        }
    }

    private void OnRosterDrop(object sender, DragEventArgs e)
    {
        if (_dragSource is null) return;

        var position = e.GetPosition(_rosterPanel);
        var target = _roster.Count - 1;
        for (var i = 0; i < _rosterPanel.Children.Count; i++)
        {
            if (_rosterPanel.Children[i] is not FrameworkElement element) continue;
            var top = element.TranslatePoint(new Point(0, 0), _rosterPanel).Y;
            if (position.Y < top + element.ActualHeight / 2)
            {
                target = i;
                break;
            }
        }

        var speaker = _dragSource.Speaker;
        _dragSource = null;

        var from = _roster.IndexOf(speaker);
        if (from < 0 || from == target) return;

        _roster.RemoveAt(from);
        _roster.Insert(Math.Clamp(target, 0, _roster.Count), speaker);
        RebuildRoster();
    }

    // ------------------------------------------------------------------ input

    private void LoadDevices()
    {
        _devices.Clear();
        try
        {
            _devices.AddRange(AudioCaptureService.GetInputDevices());
        }
        catch (Exception)
        {
            // Enumeration can fail on a machine with no audio stack at all.
        }

        if (_devices.Count == 0)
        {
            _device.DisplayText = "No input device found";
            _device.IsEnabled = false;
            return;
        }

        _device.SetItems(_devices.Select(d => (d.Name, (object)d.Id)));

        // Default to whatever Windows itself considers the input, not merely
        // the first device it happens to enumerate.
        var preferred = _devices.Any(d => d.Id == AudioDevices.DefaultDeviceNumber)
            ? AudioDevices.DefaultDeviceNumber
            : _devices[0].Id;
        _device.Select(preferred);
    }

    private void OnDeviceChanged() => StartMeter();

    private void StartMeter(bool isRetry = false)
    {
        if (_device.SelectedValue is not int deviceId) return;

        _meterWatchdog.Stop();
        _peak = 0;
        _lastSignalAt = DateTime.MinValue;
        if (!isRetry) _retriedMeter = false;

        try
        {
            _meter.Start(deviceId);
            _levelStatus.Text = "Opening the device…";
            _levelStatus.Foreground = Palette.TextMutedBrush;
            _meterWatchdog.Start();
        }
        catch (Exception ex)
        {
            _levelStatus.Text = "Could not open this device — " + ex.Message;
            _levelStatus.Foreground = Palette.RecBrush;
        }

        UpdateDisk();
    }

    /// <summary>
    /// "No signal" has two causes that look identical on a meter and need
    /// different actions, so they are reported differently.
    ///
    /// No buffers at all means the device opened but Windows is not handing
    /// over audio — the wrong input, or one held by another app, or blocked
    /// by microphone privacy. Some drivers also take several seconds to start
    /// delivering, so the meter reopens the device once before saying so.
    ///
    /// Buffers that are all silence means the device is fine and the audio is
    /// not: muted, gain at zero, or nobody talking.
    /// </summary>
    private void CheckMeter()
    {
        if (!_meter.IsRunning) return;

        var openFor = (DateTime.UtcNow - _meter.StartedAt).TotalSeconds;

        if (_meter.BuffersReceived == 0)
        {
            if (openFor > 3 && !_retriedMeter)
            {
                _retriedMeter = true;
                _levelStatus.Text = "No audio yet — reopening the device…";
                _levelStatus.Foreground = Palette.WarnBrush;
                StartMeter(isRetry: true);
                return;
            }

            if (openFor > 6)
            {
                _levelStatus.Text = "Device opened but is sending no audio — try another input, " +
                                    "or check Settings → Privacy → Microphone";
                _levelStatus.Foreground = Palette.RecBrush;
            }
            else if (openFor > 1)
            {
                _levelStatus.Text = "Waiting for the device to start…";
                _levelStatus.Foreground = Palette.TextMutedBrush;
            }
            return;
        }

        // Buffers are arriving; decay the meter here too so it falls back to
        // zero when the room goes quiet instead of freezing at the last peak.
        _peak *= 0.86;
        var width = _levelTrack.ActualWidth;
        if (width > 0) _levelFill.Width = Math.Clamp(Normalise(_peak), 0, 1) * width;
        UpdateLevelStatus();
    }

    private void OnLevel(double level)
    {
        // Decay slowly so the bar reads like a meter rather than a strobe.
        _peak = Math.Max(level, _peak * 0.86);
        if (level > 0.01) _lastSignalAt = DateTime.UtcNow;

        Dispatcher.InvokeAsync(() =>
        {
            var width = _levelTrack.ActualWidth;
            if (width <= 0) return;
            _levelFill.Width = Math.Clamp(Normalise(_peak), 0, 1) * width;
            UpdateLevelStatus();
        });
    }

    /// <summary>Map a linear peak onto the −48 dB … 0 dB scale the guide draws.</summary>
    private static double Normalise(double peak)
    {
        if (peak <= 0.0001) return 0;
        var db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 48.0) / 48.0, 0, 1);
    }

    private void UpdateLevelStatus()
    {
        if (_peak < 0.005)
        {
            var silentFor = _lastSignalAt == DateTime.MinValue
                ? (DateTime.UtcNow - _meter.StartedAt).TotalSeconds
                : (DateTime.UtcNow - _lastSignalAt).TotalSeconds;

            // The device is delivering, so this is silence rather than a dead
            // input — say which, and only nag once it has been quiet a while.
            _levelStatus.Text = silentFor > 8
                ? "Only silence from " + _meter.FormatDescription + " — is the mic muted?"
                : "Receiving " + _meter.FormatDescription + " — say something to test";
            _levelStatus.Foreground = silentFor > 8 ? Palette.WarnBrush : Palette.TextMutedBrush;
        }
        else if (_peak > 0.92)
        {
            _levelStatus.Text = "Too hot — move the mic further away";
            _levelStatus.Foreground = Palette.RecBrush;
        }
        else if (_peak < 0.06)
        {
            _levelStatus.Text = "Very quiet — move the mic closer";
            _levelStatus.Foreground = Palette.WarnBrush;
        }
        else
        {
            _levelStatus.Text = "Level good — " + (20 * Math.Log10(_peak)).ToString("0") + " dB peak";
            _levelStatus.Foreground = Palette.GoodBrush;
        }
    }

    private void UpdateDisk()
    {
        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception)
        {
            // A folder that can't be created yet is reported when Start is
            // pressed (with the full diagnostic in the Log), not here — the
            // meter and disk readout must never be what breaks on this.
        }

        var path = AppPaths.SessionsRoot;
        _sessionsRootText.Text = path;
        _sessionsRootText.ToolTip = path;
        _diskText.Text = DiskInfo.Describe(path, _options.Mp3BitrateKbps);
    }

    /// <summary>
    /// Point new sessions at a folder the operator picks. Write-probed
    /// through the same retrying create as an actual session folder would
    /// get, so a folder that turns out to be just as broken as the default
    /// is reported here — with the full diagnostic in the Log — rather than
    /// silently accepted and only discovered at Start.
    /// </summary>
    private void BrowseForSaveFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where VoxMark saves recordings",
        };

        try
        {
            if (Directory.Exists(AppPaths.SessionsRoot)) dialog.InitialDirectory = AppPaths.SessionsRoot;
        }
        catch (Exception)
        {
            // An unreadable current folder is not worth failing the dialog over.
        }

        if (dialog.ShowDialog(this) != true) return;

        var chosen = dialog.FolderName;
        try
        {
            AppPaths.CreateDirectory(chosen);
        }
        catch (Exception ex)
        {
            var hint = AppPaths.OneDriveHint(chosen);
            AppendLog("Could not use \"" + chosen + "\" as the save location.\n" + FormatException(ex) +
                      (hint.Length > 0 ? "\n" + hint : ""));
            return;
        }

        AppSettingsStore.Save(new AppSettingsStore.Settings { SessionsRoot = chosen });
        UpdateDisk();
    }

    private void ResetSaveFolder()
    {
        AppSettingsStore.Save(new AppSettingsStore.Settings { SessionsRoot = "" });
        UpdateDisk();
    }

    /// <summary>Append one timestamped, copyable entry to the Log area.</summary>
    private void AppendLog(string message)
    {
        var entry = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        _log.Text = _log.Text == "No issues yet" ? entry : _log.Text + "\n\n" + entry;
        _log.CaretIndex = _log.Text.Length;
        _log.ScrollToEnd();
    }

    /// <summary>The full exception chain — type, message, HResult — for a copyable diagnostic.</summary>
    private static string FormatException(Exception ex)
    {
        var lines = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            lines.Add(e.GetType().FullName + ": " + e.Message + " (0x" + e.HResult.ToString("X8") + ")");
        }
        return string.Join("\n", lines);
    }

    // ----------------------------------------------------------------- presets

    private void LoadPresets()
    {
        _presets = PresetStore.Load();
        RebuildPresetChips(false);
    }

    private bool _managingPresets;

    private void ToggleManagePresets()
    {
        _managingPresets = !_managingPresets;
        RebuildPresetChips(_managingPresets);
    }

    private void RebuildPresetChips(bool managing)
    {
        _presetChips.Children.Clear();

        if (_presets.Count == 0)
        {
            _presetChips.Children.Add(Ui.Text("No presets yet — save this roster below", 12.5, Palette.TextMutedBrush));
            return;
        }

        foreach (var preset in _presets)
        {
            var chip = new Button
            {
                Content = managing ? preset.ChipLabel + "   ✕" : preset.ChipLabel,
                MinHeight = 32,
                Padding = new Thickness(13, 0, 13, 0),
                Margin = new Thickness(0, 0, 8, 8),
            };
            if (TryFindResource(managing ? "ChipButton" : "ChipButtonAccent") is Style style) chip.Style = style;
            chip.FontSize = 13;

            var captured = preset;
            chip.Click += (_, _) =>
            {
                if (_managingPresets)
                {
                    _presets = PresetStore.Remove(captured.Name);
                    RebuildPresetChips(true);
                }
                else
                {
                    ApplyPreset(captured);
                }
            };
            _presetChips.Children.Add(chip);
        }
    }

    private void ApplyPreset(Preset preset)
    {
        _roster.Clear();
        foreach (var speaker in preset.Speakers.Take(12))
        {
            _roster.Add(speaker.Clone());
        }
        RebuildRoster();
    }

    private void SavePreset()
    {
        var named = _roster.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (named.Count == 0) return;

        var name = string.IsNullOrWhiteSpace(_title.Text) ? "Preset" : _title.Text.Trim();
        _presets = PresetStore.Upsert(name, named);
        _managingPresets = false;
        RebuildPresetChips(false);
    }

    // --------------------------------------------------------------- start

    private void UpdateFolderPreview()
    {
        var title = string.IsNullOrWhiteSpace(_title.Text) ? "New meeting" : _title.Text.Trim();
        var folder = System.IO.Path.Combine(
            AppPaths.SessionsRoot,
            ScheduledAt().ToString("yyyy-MM-dd") + "_" + AppPaths.Slugify(title)) + "\\";
        _folderPreview.Text = folder;
        _folderPreview.ToolTip = folder;
    }

    /// <summary>
    /// The meeting's own date and time. Free text so it can be typed ahead of
    /// the meeting; anything unparseable falls back to now and says so rather
    /// than silently filing the session under the wrong day.
    /// </summary>
    private DateTimeOffset ScheduledAt()
    {
        return TryParseDate(out var parsed) ? parsed : DateTimeOffset.Now;
    }

    private bool TryParseDate(out DateTimeOffset value)
    {
        var text = _dateTime.Text.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value)) return true;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;
        value = DateTimeOffset.Now;
        return false;
    }

    private void ValidateDate()
    {
        var ok = TryParseDate(out _);
        _dateHint.Text = ok ? "" : "unrecognised — using now";
        _dateHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        _dateTime.Foreground = ok ? Palette.TextBrush : Palette.RecBrush;
        UpdateFolderPreview();
    }

#if !VOXMARK_LITE
    /// <summary>
    /// What the operator wants, which is not always what is possible. Kept
    /// apart from <c>_options.TranscriptionEnabled</c> — which is what will
    /// actually happen — so that a model file that is temporarily missing
    /// does not quietly erase the preference. Put the file back and the
    /// toggle comes back on by itself.
    /// </summary>
    private bool _transcriptionPreferred;

    /// <summary>Guards the programmatic IsChecked writes in <see cref="RefreshTranscriptionState"/>.</summary>
    private bool _syncingTranscription;

    private void SetTranscription(bool wanted)
    {
        if (_syncingTranscription) return;

        _transcriptionPreferred = wanted;
        RememberTranscription();
        RefreshTranscriptionState();
    }

    /// <summary>Whether a meeting started right now would actually get a transcript.</summary>
    private bool CanTranscribe(out string reason)
    {
        if (WhisperRuntime.Probe() is { } runtimeProblem)
        {
            reason = runtimeProblem;
            return false;
        }

        var model = WhisperRuntime.ResolveModel(_options.WhisperModelPath);
        reason = model.Problem ?? model.Warning ?? "";
        return model.IsUsable;
    }

    /// <summary>
    /// Reconcile the preference with what this machine can do, and say which
    /// of the two the operator is looking at. This is also where
    /// <c>_options.TranscriptionEnabled</c> gets its value, so the session
    /// that starts can never promise a transcript this screen did not.
    /// </summary>
    private void RefreshTranscriptionState()
    {
        var model = WhisperRuntime.ResolveModel(_options.WhisperModelPath);
        var ready = CanTranscribe(out var reason);

        _options.TranscriptionEnabled = _transcriptionPreferred && ready;

        _modelName.Text = model.Path.Length > 0 ? model.Name : "none found";
        _modelName.Foreground = ready ? Palette.AccentTextBrush : Palette.TextMutedBrush;

        _syncingTranscription = true;
        try
        {
            _transcribeToggle.IsChecked = _transcriptionPreferred;
        }
        finally
        {
            _syncingTranscription = false;
        }

        if (!ready)
        {
            _transcribeStatus.Text = _transcriptionPreferred
                ? reason + " Recording will go ahead without a transcript."
                : reason;
            _transcribeStatus.Foreground = Palette.WarnBrush;
            return;
        }

        _transcribeStatus.Text = _transcriptionPreferred
            ? "Ready — words are recognised on this PC while you mark, and land in the Markdown "
              + "under the speaker you marked."
            : "A model is ready. Turn this on to transcribe while you record.";
        _transcribeStatus.Foreground = Palette.TextMutedBrush;
    }

    /// <summary>Pick a model file by hand, for anyone not using the Models folder.</summary>
    private void BrowseForModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a whisper speech model",
            Filter = "Whisper ggml model (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        try
        {
            if (Directory.Exists(WhisperRuntime.ModelsFolder))
            {
                dialog.InitialDirectory = WhisperRuntime.ModelsFolder;
            }
        }
        catch (Exception)
        {
            // An unreadable folder is not worth failing the dialog over.
        }

        if (dialog.ShowDialog(this) != true) return;

        _options.WhisperModelPath = dialog.FileName;
        RememberTranscription();
        RefreshTranscriptionState();
    }

    private void RememberTranscription() => TranscriptionSettingsStore.Save(new TranscriptionSettingsStore.Settings
    {
        ModelPath = _options.WhisperModelPath,
        Enabled = _transcriptionPreferred,
        Language = _options.TranscriptionLanguage,
    });
#endif

    /// <summary>Save the whole meeting — title, time, room, roster, options — for later.</summary>
    private void SaveSetup()
    {
        var title = string.IsNullOrWhiteSpace(_title.Text) ? "New meeting" : _title.Text.Trim();
        var plan = new MeetingPlan
        {
            Title = title,
            Room = _room.Text.Trim(),
            ScheduledAt = ScheduledAt(),
            Speakers = _roster.Where(sp => !string.IsNullOrWhiteSpace(sp.Name)).Select(sp => sp.Clone()).ToList(),
            Options = new SessionOptions
            {
                AllowOverlappingMarks = _options.AllowOverlappingMarks,
                MarkStartOffsetSeconds = _options.MarkStartOffsetSeconds,
                Mp3BitrateKbps = _options.Mp3BitrateKbps,
                SplitMinutes = _options.SplitMinutes,
                // The preference, not what this machine can do today: a plan
                // opened on a PC with the model installed should transcribe.
#if VOXMARK_LITE
                TranscriptionEnabled = _options.TranscriptionEnabled,
#else
                TranscriptionEnabled = _transcriptionPreferred,
#endif
            },
        };
        // Re-saving an opened plan updates it in place instead of piling up
        // near-identical copies in the library.
        if (!string.IsNullOrEmpty(_planId)) plan.Id = _planId;

        try
        {
            PlanStore.Upsert(plan);
            _planId = plan.Id;
            SetTitle("Setup — " + title);
            _levelStatus.Text = "Setup saved — it is waiting in the library";
            _levelStatus.Foreground = Palette.GoodBrush;
        }
        catch (Exception ex)
        {
            _levelStatus.Text = "Could not save the setup — " + ex.Message;
            _levelStatus.Foreground = Palette.RecBrush;
        }
    }

    private void GoBack()
    {
        _meter.Dispose();
        new LibraryWindow().Show();
        Close();
    }

    private void StartRecording()
    {
        var speakers = _roster.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (speakers.Count == 0)
        {
            _rosterHeading.Text = "ROSTER · ADD AT LEAST ONE SPEAKER";
            _rosterHeading.Foreground = Palette.RecBrush;
            return;
        }

        if (_device.SelectedValue is not int deviceId)
        {
            _levelStatus.Text = "Pick an input device first";
            _levelStatus.Foreground = Palette.RecBrush;
            return;
        }

        // Anyone left blank is dropped, and the remaining rows take the slots
        // in roster order — the ids the operator will see all session.
        for (var i = 0; i < speakers.Count; i++) speakers[i].SlotIndex = i;

        _meter.Dispose();

        var title = string.IsNullOrWhiteSpace(_title.Text) ? "New meeting" : _title.Text.Trim();
        var deviceName = _devices.FirstOrDefault(d => d.Id == deviceId).Name ?? "";

        // Not pre-created here: SessionStore.Create's own (now hardened,
        // retrying) directory creation already makes SessionsRoot and the
        // per-meeting folder together, inside the try/catch below — a
        // separate unguarded EnsureCreated() call right before it would just
        // be the same class of crash one line earlier.
        RecordingSession session;
        try
        {
            session = SessionStore.Create(title, _room.Text.Trim(), speakers, _options, deviceName,
                ScheduledAt(), DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _levelStatus.Text = "Could not create the session folder — " + ex.Message;
            _levelStatus.Foreground = Palette.RecBrush;

            var folder = AppPaths.SessionsRoot;
            var hint = AppPaths.OneDriveHint(folder);
            AppendLog("Could not create the session folder under \"" + folder + "\".\n" +
                      FormatException(ex) +
                      (hint.Length > 0 ? "\n" + hint : "") +
                      "\nYou can pick a different folder under \"Save recordings to\" above.");
            return;
        }

        var recording = new RecordingWindow(session, deviceId);
        recording.Show();
        Close();
    }
}
