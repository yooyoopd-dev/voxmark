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
    /// <summary>
    /// The height budget. Adding the transcript strip does not steal from the
    /// timecode, the tiles' legibility or the Stop button — it is paid for
    /// out of the live waveform's slack and the collapsed dock's chrome, and
    /// the numbers live together here so that stays checkable.
    ///
    /// The three wave heights are the same 1.5x scale-up of one another
    /// throughout — compact and expanded stay proportionally smaller than
    /// tall — so the tile grid's own scroller is what absorbs the extra
    /// height rather than the timecode or the dock.
    /// </summary>
    private const double TallWaveHeight = 198;
    private const double CompactWaveHeight = 162;
    private const double ExpandedWaveHeight = 108;
    private const double CollapsedDockHeight = 232;
    private const double CompactCollapsedDockHeight = 150;
    private const double ExpandedDockHeight = 380;

    private readonly RecordingSession _session;
    private readonly AudioCaptureService _capture;
    private readonly MarkingEngine _marking;
    private readonly MarkJournal _journal;
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly ToastWindow _toast = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ConcurrentQueue<WaveSlice> _slices = new();

    /// <summary>
    /// Recognised lines handed over from the worker thread and drained on the
    /// UI timer — the same arrangement as <see cref="_slices"/>, and for the
    /// same reason: nothing in the UI gets to sit on a background thread.
    /// </summary>
    private readonly ConcurrentQueue<TranscriptSegment> _recognised = new();
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

    /// <summary>
    /// The rename / remove card, shown over the tile it belongs to. A popup
    /// rather than a dialog for the same reason the dock has no modals
    /// (section 06): the recording UI never disappears, and nothing here can
    /// block the operator from marking the next speaker — Esc or a click
    /// elsewhere dismisses it, and the global hotkeys keep working while it
    /// is open.
    /// </summary>
    private readonly Popup _cardPopup = new()
    {
        Placement = PlacementMode.Center,
        StaysOpen = false,
        AllowsTransparency = true,
        PopupAnimation = PopupAnimation.Fade,
    };

    /// <summary>
    /// The live transcript strip and its header. Present only when speech
    /// recognition is actually running: an empty strip would cost the speaker
    /// grid height for nothing.
    ///
    /// The explicit nulls are for the Lite build, which never assigns any of
    /// these — without them the compiler warns four times on every Lite
    /// publish about fields that are deliberately absent there.
    /// </summary>
    private readonly TranscriptView? _transcriptView = null;
    private readonly TextBlock? _transcriptStatus = null;
    private readonly UIElement? _transcriptRow = null;
    private readonly TranscriptStore? _transcriptStore = null;
#if !VOXMARK_LITE
    private readonly TranscriptionService? _transcription;
#endif

    /// <summary>True when the transcript strip is on screen and the layout paid for it.</summary>
    private readonly bool _compactLayout;

#if !VOXMARK_LITE
    /// <summary>
    /// Holds a one-off transcription message on screen for a few seconds. The
    /// tick rewrites that status ten times a second, so without this a
    /// "couldn't recognise that chunk" would be gone before it was read.
    /// </summary>
    private DateTime _transcriptNoticeUntil = DateTime.MinValue;
#endif

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
        // Set for real in BuildBody, once it is known whether the transcript
        // strip is taking a slice of this screen's height.
        _waveWell.Height = TallWaveHeight;

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
        _dockHost = new Border { Child = _dock };
        _dock.LayoutChanged += OnDockLayoutChanged;

#if !VOXMARK_LITE
        // Built before the body, because whether it starts at all decides how
        // much height the waveform, the dock and the tiles get.
        if (session.Options.TranscriptionEnabled)
        {
            _transcription = new TranscriptionService(session.Options);
            _transcriptView = new TranscriptView();
            _transcriptStatus = Ui.Text("starting…", 11, Palette.TextMutedBrush);
            // Anything long enough to need more room than this belongs in the
            // notice banner, which is where the real failures already go.
            _transcriptStatus.MaxWidth = 200;
            _transcriptRow = BuildTranscriptRow();
            try
            {
                _transcriptStore = new TranscriptStore(session.TranscriptPath);
            }
            catch (Exception)
            {
                // Losing the journal costs the text after a crash, not the
                // text on screen and certainly not the recording.
            }
        }
#endif

        _compactLayout = _transcriptRow is not null;
        _dockHost.MaxHeight = _compactLayout ? CompactCollapsedDockHeight : CollapsedDockHeight;

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

        // A popup lives in its own window, so it would otherwise stay on
        // screen after this one is minimised into the background — with the
        // tile it belongs to nowhere in sight.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseCardPopup(); };

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
        if (_transcriptRow is not null) top.Children.Add(_transcriptRow);
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
        _waveWell.Height = _compactLayout ? CompactWaveHeight : TallWaveHeight;
        _waveWell.Margin = new Thickness(20, _compactLayout ? 10 : 14, 20, 0);
        return _waveWell;
    }

    /// <summary>
    /// The transcript strip and its header. Recognition runs a few seconds
    /// behind the room by nature, so the header says so rather than letting
    /// the lag read as a fault.
    /// </summary>
    private UIElement BuildTranscriptRow()
    {
        var row = Ui.Columns(1,
            Fixed(Ui.Section("Transcript"), 64),
            _transcriptView!,
            PadLeft(_transcriptStatus!, 10));
        row.Margin = new Thickness(20, 8, 20, 0);
        return row;
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
            var tile = new SpeakerTile(speaker, density, _compactLayout) { Margin = new Thickness(5) };
            tile.Tapped += slot => ToggleSpeaker(slot, viaHotkey: false);
            tile.EditRequested += ShowRenameCard;
            tile.DeleteRequested += ShowRemoveCard;
            _tileGrid.Children.Add(tile);
            _tiles[speaker.SlotIndex] = tile;
        }

        if (count < 12)
        {
            var add = new Border
            {
                Height = SpeakerTile.HeightFor(density, _compactLayout),
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
        StartTranscription();
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

    /// <summary>
    /// Bring speech recognition up beside the recorder. Everything here is
    /// allowed to fail: recognition is a passive tap, and a meeting that
    /// records without a transcript is a far better outcome than one that
    /// does not record at all (section 11).
    /// </summary>
    private void StartTranscription()
    {
#if !VOXMARK_LITE
        if (_transcription is null || _transcriptView is null || _transcriptStatus is null) return;

        _transcription.SegmentRecognised += OnSegmentRecognised;
        _transcription.StatusChanged += OnTranscriptionStatus;

        // Two halves, and the tap goes on between them. Loading a model takes
        // seconds — weights off disk plus a first CUDA context — and until
        // v1.2.7 the tap was attached after all of it, so the opening of the
        // meeting was never transcribed and, worse, the pipeline treated its
        // own first sample as time zero and reported every timestamp early by
        // that gap for the rest of the session. Now the audio queues from the
        // first buffer and carries the recorder's own file time with it.
        var problem = _transcription.Prepare(_capture.CurrentFormat);
        if (problem is null)
        {
            _capture.PcmAvailable += _transcription.Push;
            problem = _transcription.Begin();
            if (problem is not null) _capture.PcmAvailable -= _transcription.Push;
        }

        if (problem is not null)
        {
            // Named in the banner where the operator will see it, exactly as
            // a refused global hotkey is, rather than failing quietly — and
            // the strip says it too, so the empty space is explained rather
            // than looking like recognition that never got going.
            ShowNotice(problem + " The meeting is still recording.");
            _transcriptView.ShowUnavailable("No transcript for this meeting — see the notice above.");
            _transcriptStatus.Text = "unavailable";
            _transcriptNoticeUntil = DateTime.MaxValue;
            return;
        }

        _session.TranscriptionDescription = _transcription.Description;

        // Recognition is running, so this is not the failure path — but an
        // NVIDIA machine that fell back to the CPU is about five times slower
        // than it should be, and the operator would otherwise only see that
        // as a transcript drifting further behind the room.
        // Never over the top of a notice that is already up: a device that
        // failed to open is worse news than an engine that is merely slow.
        if (_transcription.RuntimeWarning is { } warning && _noticeBanner.Visibility != Visibility.Visible)
        {
            ShowNotice(warning);
        }
#endif
    }

#if !VOXMARK_LITE
    /// <summary>
    /// A recognised line, arriving on the worker thread. The speaker colour
    /// is resolved here from the marks as they stand right now, which is what
    /// makes the strip show the same attribution the Markdown will make.
    /// </summary>
    private void OnSegmentRecognised(TranscriptSegment segment)
    {
        // Journalled on this thread so a crash cannot lose it, then queued
        // for the UI. The journal is the durable record; the queue is a view.
        _transcriptStore?.Append(segment);
        _recognised.Enqueue(segment);
    }

    private void OnTranscriptionStatus(string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_transcriptStatus is null) return;
            _transcriptStatus.Text = status;
            _transcriptStatus.Foreground = Palette.TextMutedBrush;
            _transcriptNoticeUntil = DateTime.UtcNow.AddSeconds(6);
        });
    }
#endif

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

            // A replacement device can be a different sample rate, which the
            // recorder handles by rolling into a new file — so audio_format
            // is no longer what it was when the meeting started.
            _session.AudioFormatDescription = _capture.FormatDescription;
            _session.AudioParts = _capture.Parts.ToList();
            UpdatePartLabel();

            ShowNotice(message);
            PersistSession(_capture.ElapsedSeconds);
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

        UpdateSpeakingNow();

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
        DrainRecognised();
        UpdateTranscriptStatus();

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

    /// <summary>The header's "Speaking now", which is also what a reassign changes.</summary>
    private void UpdateSpeakingNow()
    {
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
    }

    /// <summary>
    /// Move recognised lines onto the session and into the strip. The speaker
    /// colour is resolved against the marks as they stand right now, so the
    /// strip shows the attribution the Markdown will actually make.
    /// </summary>
    private void DrainRecognised()
    {
        while (_recognised.TryDequeue(out var segment))
        {
            _session.Transcript.Add(segment);

            var mark = TranscriptMapper.MarkFor(segment, AttributionMarks());
            _transcriptView?.Append(segment, mark is not null ? Palette.ForSlot(mark.SpeakerSlot) : (Color?)null);
        }
    }

    /// <summary>
    /// The marks a transcript line is coloured against: the closed ones, plus
    /// whatever is open right now standing in for the mark it will become.
    ///
    /// Without the open ones a line recognised during the current turn has
    /// nobody to belong to and is drawn grey until the operator closes the
    /// mark — which is exactly the stretch they are watching, and exactly when
    /// a reassign has to be visible. The stand-ins are throwaway: negative ids
    /// so they can never be confused with a journalled mark, and rebuilt on
    /// every call rather than stored.
    /// </summary>
    private IReadOnlyList<Mark> AttributionMarks()
    {
        if (_marking.Open.Count == 0) return _marking.Marks;

        var now = _capture.ElapsedSeconds;
        var marks = _marking.Marks.ToList();
        var id = 0L;
        foreach (var open in _marking.Open)
        {
            marks.Add(new Mark
            {
                Id = --id,
                SpeakerSlot = open.SpeakerSlot,
                StartSeconds = open.StartSeconds,
                EndSeconds = Math.Max(open.StartSeconds, now),
            });
        }

        return marks;
    }

    /// <summary>
    /// How far recognition is running behind the room. Whisper decodes in
    /// chunks, so a handful of seconds is normal and is labelled as such;
    /// only a backlog that keeps growing is worth an operator's attention.
    /// </summary>
    private void UpdateTranscriptStatus()
    {
#if !VOXMARK_LITE
        if (_transcription is null || _transcriptStatus is null || !_transcription.IsRunning) return;
        if (DateTime.UtcNow < _transcriptNoticeUntil) return;

        var backlog = _transcription.BacklogSeconds;
        var dropped = _transcription.DroppedSeconds;

        _transcriptStatus.Text = dropped >= 1
            ? "⚠ " + dropped.ToString("0") + " s not transcribed"
            : backlog > 25
                ? backlog.ToString("0") + " s behind"
                : _transcription.Description;

        _transcriptStatus.Foreground = dropped >= 1 ? Palette.WarnBrush : Palette.TextMutedBrush;
#endif
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
        var count = Math.Max(1, _capture.Parts.Count);

        if (_session.Options.SplitMinutes <= 0)
        {
            // Normally nothing to say — but an input device that changed
            // format mid-meeting rolls a new file whether a split was asked
            // for or not, and the operator should not have to discover that
            // in the folder afterwards.
            _partLabel.Text = count > 1 ? "file " + count + " · rolled after an input change" : "";
            if (count > 1) _partLabel.Margin = new Thickness(0, 0, 20, 0);
            return;
        }

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

        // Reassigning the live mark from the dock has to land everywhere at
        // once, not on the next tick: the header, the tile that is lit, and
        // the flag over the waveform all name the speaker the operator just
        // corrected. A tenth of a second late reads as "did that work?", and
        // the operator asks again.
        var elapsed = _capture.ElapsedSeconds;
        UpdateSpeakingNow();
        UpdateTiles(elapsed);
        _waveform.SetBoundaries(BuildBoundaries(elapsed));
        _waveform.InvalidateVisual();

        // A live-repair edit (reassign, nudge, merge, split, delete,
        // undo/redo) can change which mark a transcript line was resolved
        // against; already-drawn lines otherwise keep the colour they were
        // given the moment they first appeared. Re-resolving all of them
        // here is cheap — only the timecode labels repaint — and this fires
        // on every marking change regardless of edition, so it's a no-op
        // (null strip) whenever transcription isn't running.
        var attribution = AttributionMarks();
        _transcriptView?.RecolorAll(s => TranscriptMapper.MarkFor(s, attribution) is { } m
            ? Palette.ForSlot(m.SpeakerSlot)
            : (Color?)null);
    }

    private void OnDockLayoutChanged()
    {
        // Expanding takes space from the grid's spare height and from the
        // live waveform, never from the timecode, the tiles or the Stop
        // button. The heights themselves are the budget at the top of this
        // file, which the transcript strip also draws on.
        _waveWell.Height = _dock.IsExpanded
            ? ExpandedWaveHeight
            : _compactLayout ? CompactWaveHeight : TallWaveHeight;
        _dockHost.MaxHeight = _dock.IsExpanded
            ? ExpandedDockHeight
            : _compactLayout ? CompactCollapsedDockHeight : CollapsedDockHeight;
    }

    // -------------------------------------------------------------- marking

    private void ToggleSpeaker(int slot, bool viaHotkey)
    {
        if (_awaitingStopConfirm || _stopping) return;

        var elapsed = _capture.ElapsedSeconds;
        var result = _marking.Toggle(slot, elapsed);

        // The boundary is the new turn's own start when one opened — which is
        // the press time less the mark offset — and the press itself when the
        // tap only closed something.
        NoteBoundary(_marking.Open.FirstOrDefault(o => o.SpeakerSlot == slot)?.StartSeconds ?? elapsed);

        if (viaHotkey && !IsActive)
        {
            _toast.ShowMark(_session, result, elapsed);
        }
    }

    /// <summary>
    /// Tell speech recognition where a turn changed hands, so it can end a
    /// chunk there.
    ///
    /// Whisper picks its own segment boundaries and knows nothing about the
    /// roster, so a chunk holding the end of one speaker and the start of the
    /// next can come back as a single segment — which attribution then has to
    /// award whole, putting a sentence under the wrong name. A chunk that
    /// ends on the handover cannot produce that segment in the first place.
    ///
    /// A no-op in Lite, and while transcription is off or has failed.
    /// </summary>
    private void NoteBoundary(double fileSeconds)
    {
#if !VOXMARK_LITE
        _transcription?.NoteSpeakerChange(fileSeconds);
#endif
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
        // While a tile's rename card is open the digits belong to the name
        // being typed, not to the roster. Esc closes it; everything else is
        // left to the card's own field and buttons. The Alt+n global hotkeys
        // are registered with Windows and never come through here, so marking
        // by hotkey still works with the card open.
        if (_cardPopup.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                CloseCardPopup();
                e.Handled = true;
            }
            return;
        }

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
                // Only a turn that actually ended is a handover; Space on an
                // idle grid should not cost the chunker a cut.
                if (_marking.CloseAll(_capture.ElapsedSeconds).ClosedSlot is not null)
                {
                    NoteBoundary(_capture.ElapsedSeconds);
                }
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

    // --------------------------------------------------- rename / remove card

    /// <summary>
    /// Rename the speaker on one tile. The roster slot, the key, the colour
    /// and every mark already made against it are untouched — only the label
    /// changes, which is what makes this safe to do mid-meeting: the operator
    /// misheard a name, not who was talking.
    /// </summary>
    private void ShowRenameCard(int slot)
    {
        if (_stopping) return;
        if (_session.SpeakerForSlot(slot) is not { } speaker) return;
        if (!_tiles.TryGetValue(slot, out var tile)) return;

        var field = new TextBox
        {
            Text = speaker.Name,
            FontSize = 15,
            MinWidth = 200,
            Foreground = Palette.TextBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        void Commit()
        {
            var name = field.Text.Trim();
            CloseCardPopup();
            if (name.Length == 0 || name == speaker.Name) return;

            speaker.Name = name;
            if (_tiles.TryGetValue(slot, out var renamed)) renamed.SetName(name);

            // Everything that prints a name reads it from the roster, so the
            // dock, the toast and the waveform flags only need telling that
            // it changed.
            _toast.SetRoster(_session.Speakers);
            _dock.Refresh();
            _dock.ShowNotice("Renamed to " + name, false);
            PersistSession(_capture.ElapsedSeconds);
        }

        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseCardPopup(); e.Handled = true; }
        };

        var save = Ui.MakeButton("Save", "Enter", "ChipButtonAccent", (_, _) => Commit());
        var cancel = Ui.MakeButton("Cancel", "Esc", "ChipButton", (_, _) => CloseCardPopup());
        cancel.Margin = new Thickness(8, 0, 0, 0);

        var body = Ui.Vertical(10,
            Ui.Section("Rename speaker"),
            Ui.Well(Ui.Vertical(0, Ui.Text("Name", 10, Palette.TextMutedBrush), field),
                new Thickness(10, 6, 10, 6), 6),
            Ui.Columns(0, Ui.Filler(), save, cancel));

        OpenCardPopup(tile, body);

        // The popup builds its own window when it opens, so the field is not
        // focusable until that has happened — hence the queued focus rather
        // than one on the line after.
        Dispatcher.InvokeAsync(() =>
        {
            field.Focus();
            field.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Remove a speaker from the roster, behind two confirmations.
    ///
    /// A speaker who has already been marked is not removable, and the second
    /// step says so rather than asking again: their marks address this slot,
    /// and a roster without it would export rows attributed to "Unknown". The
    /// repair for a wrong name is the ✎ beside this, which keeps the marks.
    /// </summary>
    private void ShowRemoveCard(int slot)
    {
        if (_stopping) return;
        if (_session.SpeakerForSlot(slot) is not { } speaker) return;
        if (!_tiles.TryGetValue(slot, out var tile)) return;

        var marked = _marking.Marks.Count(m => m.SpeakerSlot == slot) + (_marking.IsOpen(slot) ? 1 : 0);
        if (marked > 0)
        {
            var rename = Ui.MakeButton("✎ Rename instead", null, "ChipButtonAccent", (_, _) =>
            {
                CloseCardPopup();
                ShowRenameCard(slot);
            });
            var close = Ui.MakeButton("Close", "Esc", "ChipButton", (_, _) => CloseCardPopup());
            close.Margin = new Thickness(8, 0, 0, 0);

            OpenCardPopup(tile, Ui.Vertical(10,
                Ui.Section("Cannot remove " + speaker.Name, Palette.WarnBrush),
                Wrapped(marked + (marked == 1 ? " mark is" : " marks are") + " already attributed to " +
                        speaker.Name + " in this recording. Removing the card would leave those rows " +
                        "with nobody to name in the export, so the card stays."),
                Ui.Columns(0, Ui.Filler(), rename, close)));
            return;
        }

        ShowRemoveConfirm(tile, speaker, second: false);
    }

    private void ShowRemoveConfirm(SpeakerTile tile, Speaker speaker, bool second)
    {
        var confirm = Ui.MakeButton(second ? "Yes, remove" : "Remove", null, "DangerButton", (_, _) =>
        {
            if (second) RemoveSpeaker(speaker);
            else ShowRemoveConfirm(tile, speaker, second: true);
        });
        var cancel = Ui.MakeButton("Cancel", "Esc", "ChipButton", (_, _) => CloseCardPopup());
        cancel.Margin = new Thickness(8, 0, 0, 0);

        OpenCardPopup(tile, Ui.Vertical(10,
            Ui.Section(second ? "Remove " + speaker.Name + "?" : "Remove speaker", Palette.RecTextBrush),
            Wrapped(second
                ? "This is the second ask. " + speaker.Name + " (" + speaker.KeyLabel +
                  ") leaves the grid and the key stops marking. Nothing already recorded changes."
                : speaker.Name + " has not been marked yet, so the card can go. Their slot " +
                  "number is never reused, so the other speakers keep their colours and keys."),
            Ui.Columns(0, Ui.Filler(), confirm, cancel)));
    }

    private void RemoveSpeaker(Speaker speaker)
    {
        CloseCardPopup();
        _session.Speakers.Remove(speaker);

        // The key is free again the moment the card is gone, so the global
        // registrations are rebuilt rather than left holding it.
        _hotkeys.UnregisterAll();
        _hotkeys.Register(_session.Speakers.Select(s => s.Key));

        BuildTiles();
        _dock.Refresh();
        _dock.ShowNotice("Removed " + speaker.Name + " from the roster", false);
        PersistSession(_capture.ElapsedSeconds);
    }

    private void OpenCardPopup(SpeakerTile tile, UIElement body)
    {
        _cardPopup.IsOpen = false;
        _cardPopup.PlacementTarget = tile;

        var card = Ui.Card(body, new Thickness(14), Palette.AccentBrush);
        card.BorderThickness = new Thickness(1.5);
        card.MinWidth = 260;
        card.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 4, Opacity = 0.55 };
        _cardPopup.Child = card;
        _cardPopup.IsOpen = true;
    }

    private void CloseCardPopup()
    {
        if (!_cardPopup.IsOpen && _cardPopup.Child is null) return;

        _cardPopup.IsOpen = false;
        _cardPopup.Child = null;

        // The keys go back to marking the moment the card is gone — the same
        // handover the add-speaker strip makes when it closes.
        Focus();
    }

    private static FrameworkElement Wrapped(string text)
    {
        var block = Ui.Wrap(text, 12.5, Palette.TextBodyBrush);
        block.MaxWidth = 300;
        return block;
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

        CloseCardPopup();
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

        StopTranscription();

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

    /// <summary>
    /// Let recognition finish what it has queued, then let it go. The wait is
    /// bounded because this is the Stop path: the operator pressing Stop must
    /// never be left watching a spinner while a GPU catches up, and anything
    /// unfinished is still complete in the MP3.
    /// </summary>
    private void StopTranscription()
    {
#if !VOXMARK_LITE
        if (_transcription is not null)
        {
            _capture.PcmAvailable -= _transcription.Push;
            _transcription.StopAndFlush(TimeSpan.FromSeconds(8));
            _transcription.SegmentRecognised -= OnSegmentRecognised;
            _transcription.StatusChanged -= OnTranscriptionStatus;

            // The timer is already stopped by now, so the last chunks the
            // flush produced are still sitting in the queue.
            DrainRecognised();

            _session.TranscriptionDroppedSeconds = _transcription.DroppedSeconds;
            _session.Transcript = _session.Transcript.OrderBy(t => t.StartSeconds).ToList();

            _transcription.Dispose();
        }
#endif
        _transcriptStore?.Dispose();
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
