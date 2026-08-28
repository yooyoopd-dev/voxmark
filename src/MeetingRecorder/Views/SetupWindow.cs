using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly StackPanel _rosterPanel = new();
    private readonly WrapPanel _presetChips = new();
    private readonly TextBlock _rosterHeading;
    private readonly TextBlock _folderPreview;
    private readonly CheckBox _overlapToggle;
    private readonly Dropdown _offset;
    private readonly Dropdown _split;

    private List<Preset> _presets = new();
    private RosterRow? _capturingRow;
    private RosterRow? _dragSource;
    private double _peak;
    private string _planId = "";

    /// <summary>
    /// A blank setup, or one seeded from a meeting the operator saved
    /// earlier — same screen either way, so a plan is just a starting point
    /// that can still be edited before Start.
    /// </summary>
    public SetupWindow(MeetingPlan? plan = null)
        : base(plan is null ? "New meeting — setup" : "Setup — " + plan.Title, 1180, 800)
    {
        MinWidth = 980;
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
            ("One file", 0), ("Every 10 min", 10), ("Every 15 min", 15),
            ("Every 30 min", 30), ("Every 60 min", 60),
        });
        _split.SelectionChanged += value =>
        {
            if (value is int minutes) _options.SplitMinutes = minutes;
        };

        if (plan is not null)
        {
            _planId = plan.Id;
            _options.AllowOverlappingMarks = plan.Options.AllowOverlappingMarks;
            _options.MarkStartOffsetSeconds = plan.Options.MarkStartOffsetSeconds;
            _options.Mp3BitrateKbps = plan.Options.Mp3BitrateKbps;
            _options.SplitMinutes = plan.Options.SplitMinutes;
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

        _meter.LevelChanged += OnLevel;
        _title.TextChanged += (_, _) => UpdateFolderPreview();

        Loaded += (_, _) =>
        {
            StartMeter();
            _title.Focus();
            _title.SelectAll();
        };
        Closed += (_, _) => _meter.Dispose();
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

        var inputCard = Ui.Card(Ui.Vertical(10,
            deviceRow,
            _levelTrack,
            scale,
            Ui.Rule(),
            diskRow), new Thickness(13));

        return Ui.Vertical(0,
            WithMargin(Ui.Section("Meeting"), 0, 0, 0, 10),
            WithMargin(titleCard, 0, 0, 0, 8),
            WithMargin(dateRow, 0, 0, 0, 20),
            WithMargin(presetHeader, 0, 0, 0, 10),
            WithMargin(_presetChips, 0, 0, 0, 20),
            WithMargin(Ui.Section("Input check"), 0, 0, 0, 10),
            inputCard);
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

        var optionsCard = Ui.Card(Ui.Vertical(0,
            Pad(format, 11),
            Ui.Rule(),
            Pad(overlap, 11),
            Ui.Rule(),
            Pad(offsetRow, 11),
            Ui.Rule(),
            Pad(splitRow, 11)), new Thickness(13, 2, 13, 2));

        var offsetNote = Ui.Wrap(
            "A human presses the key after the speaker has already begun, so every mark start is shifted back " +
            "by the offset automatically; the raw press time is kept in the log so it can be re-tuned later. " +
            "Splitting rolls to a new MP3 on the chosen interval and writes a matching Markdown for each one — " +
            "timestamps keep counting from the first file, so they mean the same thing in every chunk.",
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

        var row = Ui.Columns(3,
            back,
            Ui.Text("Session folder", 12.5, Palette.TextMutedBrush),
            Pad(_folderPreview, 0, 10),
            Ui.Filler(),
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
        if (_roster.Count == 0) AddSpeaker();
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
        _device.Select(_devices[0].Id);
    }

    private void OnDeviceChanged() => StartMeter();

    private void StartMeter()
    {
        if (_device.SelectedValue is not int deviceId) return;
        try
        {
            _meter.Start(deviceId);
            _levelStatus.Text = "Say something to test the level";
            _levelStatus.Foreground = Palette.TextMutedBrush;
        }
        catch (Exception ex)
        {
            _levelStatus.Text = "Could not open this device — " + ex.Message;
            _levelStatus.Foreground = Palette.RecBrush;
        }
        UpdateDisk();
    }

    private void OnLevel(double level)
    {
        // Decay slowly so the bar reads like a meter rather than a strobe.
        _peak = Math.Max(level, _peak * 0.86);
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
            _levelStatus.Text = "No signal — say something to test";
            _levelStatus.Foreground = Palette.TextMutedBrush;
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
        AppPaths.EnsureCreated();
        _diskText.Text = DiskInfo.Describe(AppPaths.SessionsRoot, _options.Mp3BitrateKbps);
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
        _folderPreview.Text = System.IO.Path.Combine(
            AppPaths.SessionsRoot,
            ScheduledAt().ToString("yyyy-MM-dd") + "_" + AppPaths.Slugify(title)) + "\\";
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
        AppPaths.EnsureCreated();

        var title = string.IsNullOrWhiteSpace(_title.Text) ? "New meeting" : _title.Text.Trim();
        var deviceName = _devices.FirstOrDefault(d => d.Id == deviceId).Name ?? "";

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
            return;
        }

        var recording = new RecordingWindow(session, deviceId);
        recording.Show();
        Close();
    }
}
