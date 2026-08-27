using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeetingRecorder.Models;
using MeetingRecorder.Services;

namespace MeetingRecorder.Views;

public partial class RecordingWindow : Window
{
    // Speaker palette — design guide section 02, "12 fixed slots".
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x91, 0x84, 0xd9), Color.FromRgb(0xd1, 0x82, 0xc9), Color.FromRgb(0xe5, 0x80, 0x95),
        Color.FromRgb(0xe5, 0x8e, 0x6c), Color.FromRgb(0xc9, 0xa0, 0x4f), Color.FromRgb(0xa2, 0xb0, 0x4f),
        Color.FromRgb(0x74, 0xbd, 0x6f), Color.FromRgb(0x4e, 0xc3, 0x9b), Color.FromRgb(0x4e, 0xc1, 0xbd),
        Color.FromRgb(0x5f, 0xb8, 0xde), Color.FromRgb(0x7a, 0xab, 0xf0), Color.FromRgb(0x8f, 0x8f, 0xa8),
    };

    private static readonly SolidColorBrush TileBackgroundBrush = new(Color.FromRgb(0x23, 0x25, 0x32));
    private static readonly SolidColorBrush TileBorderBrush = new(Color.FromRgb(0x3f, 0x42, 0x4d));
    private static readonly SolidColorBrush MutedTextBrush = new(Color.FromRgb(0x75, 0x79, 0x8c));

    private const int LevelBarCount = 180;

    private readonly RecordingSession _session;
    private readonly AudioCaptureService _capture = new();
    private readonly MarkingEngine _marking = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<double> _levelHistory = new();
    private readonly List<Rectangle> _levelBarRects = new();
    private readonly Dictionary<int, Border> _tileBySlot = new();

    private bool _awaitingStopConfirm;
    private DateTime? _pausedAt;
    private double _pausedTotalSeconds;
    private int _pauseCount;

    public RecordingWindow(RecordingSession session, int deviceNumber)
    {
        InitializeComponent();
        _session = session;

        BuildLevelBars();
        BuildTileGrid();

        _capture.LevelChanged += OnLevelChanged;
        _uiTimer.Tick += (_, _) => Refresh();
        _uiTimer.Start();

        _session.StartedAt = DateTimeOffset.Now;
        _capture.Start(deviceNumber, _session.Mp3Path);

        Closing += OnClosing;
    }

    private void BuildLevelBars()
    {
        LevelBarsPanel.Children.Clear();
        _levelBarRects.Clear();
        _levelHistory.Clear();

        for (var i = 0; i < LevelBarCount; i++)
        {
            var rect = new Rectangle
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(1, 0, 1, 0),
                Fill = MutedTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _levelBarRects.Add(rect);
            LevelBarsPanel.Children.Add(rect);
            _levelHistory.Add(0);
        }
    }

    private void BuildTileGrid()
    {
        var count = _session.Speakers.Count;
        TileGrid.Columns = count <= 4 ? 2 : count <= 9 ? 3 : 4;
        var tileHeight = count <= 4 ? 148 : count <= 6 ? 116 : count <= 9 ? 96 : 76;

        TileGrid.Children.Clear();
        _tileBySlot.Clear();

        foreach (var speaker in _session.Speakers)
        {
            var slot = speaker.SlotIndex;
            var baseColor = Palette[slot % Palette.Length];
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, baseColor.R, baseColor.G, baseColor.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, baseColor.R, baseColor.G, baseColor.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(5),
                Height = tileHeight,
                Padding = new Thickness(12),
                Cursor = Cursors.Hand,
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = $"{speaker.KeyLabel} · {speaker.Name}",
                Foreground = Brushes.White,
                FontSize = 18,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = speaker.Role,
                Foreground = MutedTextBrush,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            });
            border.Child = stack;

            border.MouseLeftButtonUp += (_, _) => ToggleSpeaker(slot);

            TileGrid.Children.Add(border);
            _tileBySlot[slot] = border;
        }
    }

    private void OnLevelChanged(double level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _levelHistory.Add(level);
            if (_levelHistory.Count > LevelBarCount) _levelHistory.RemoveAt(0);
        });
    }

    private void Refresh()
    {
        var elapsed = _capture.ElapsedSeconds;
        ClockText.Text = FormatClock(elapsed);
        MarkCountText.Text = $"Marks {_marking.Marks.Count}";
        StatusText.Text = _capture.IsPaused ? "일시정지 (Paused)" : BuildActiveStatus();

        for (var i = 0; i < _levelBarRects.Count && i < _levelHistory.Count; i++)
        {
            _levelBarRects[i].Height = 4 + _levelHistory[i] * 56;
        }

        foreach (var (slot, border) in _tileBySlot)
        {
            var isActive = _marking.ActiveSlot == slot;
            var baseColor = Palette[slot % Palette.Length];
            border.BorderThickness = new Thickness(isActive ? 2 : 1);
            border.BorderBrush = new SolidColorBrush(isActive ? baseColor : Color.FromArgb(80, baseColor.R, baseColor.G, baseColor.B));
            border.Background = new SolidColorBrush(isActive ? Color.FromArgb(80, baseColor.R, baseColor.G, baseColor.B) : Color.FromArgb(40, baseColor.R, baseColor.G, baseColor.B));
        }
        PauseButton.Content = _capture.IsPaused ? "▶ Resume" : "⏸ Pause";
    }

    private string BuildActiveStatus()
    {
        if (_marking.ActiveSlot is not int slot) return "발화 없음 (nobody speaking)";
        var speaker = _session.Speakers.First(s => s.SlotIndex == slot);
        return $"발화 중: {speaker.Name}";
    }

    private static string FormatClock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void ToggleSpeaker(int slot)
    {
        if (_capture.IsPaused || _awaitingStopConfirm) return;
        _marking.Toggle(slot, _capture.ElapsedSeconds);
        RefreshMarksList();
    }

    private void RefreshMarksList()
    {
        var items = _marking.Marks
            .OrderByDescending(m => m.EndSeconds)
            .Take(8)
            .Select(m =>
            {
                var speaker = _session.Speakers.First(s => s.SlotIndex == m.SpeakerSlot);
                return $"{speaker.KeyLabel}  {speaker.Name,-16} {FormatClock(m.StartSeconds)} → {FormatClock(m.EndSeconds)}  ({m.DurationSeconds:0.0}s)";
            })
            .ToList();
        MarksList.ItemsSource = items;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_awaitingStopConfirm)
        {
            if (e.Key == Key.Enter) { ConfirmStop(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelStopConfirm(); e.Handled = true; }
            return;
        }

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.Z) { _marking.Undo(); RefreshMarksList(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.P) { TogglePause(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { BeginStopConfirm(); e.Handled = true; return; }
        if (e.Key == Key.Space) { _marking.CloseWithoutOpening(_capture.ElapsedSeconds); RefreshMarksList(); e.Handled = true; return; }

        var slot = KeyToSlot(e.Key, shift);
        if (slot is int s && s < _session.Speakers.Count)
        {
            ToggleSpeaker(s);
            e.Handled = true;
        }
    }

    /// <summary>1-9,0 → slots 0-9. Shift+1/Shift+2 → slots 10/11 (speakers 11-12); Shift+3..0 are unbound.</summary>
    private static int? KeyToSlot(Key key, bool shift) => key switch
    {
        Key.D1 or Key.NumPad1 => shift ? 10 : 0,
        Key.D2 or Key.NumPad2 => shift ? 11 : 1,
        Key.D3 or Key.NumPad3 => shift ? null : 2,
        Key.D4 or Key.NumPad4 => shift ? null : 3,
        Key.D5 or Key.NumPad5 => shift ? null : 4,
        Key.D6 or Key.NumPad6 => shift ? null : 5,
        Key.D7 or Key.NumPad7 => shift ? null : 6,
        Key.D8 or Key.NumPad8 => shift ? null : 7,
        Key.D9 or Key.NumPad9 => shift ? null : 8,
        Key.D0 or Key.NumPad0 => shift ? null : 9,
        _ => null,
    };

    private void TogglePause()
    {
        if (_capture.IsPaused)
        {
            _capture.Resume();
            if (_pausedAt is DateTime pausedAt)
            {
                _pausedTotalSeconds += (DateTime.Now - pausedAt).TotalSeconds;
            }
            _pausedAt = null;
        }
        else
        {
            // Pause also closes whatever mark is open, the same as Stop, so
            // the timeline the operator sees after resuming stays
            // continuous — because the file is.
            if (_marking.ActiveSlot is not null)
            {
                _marking.CloseWithoutOpening(_capture.ElapsedSeconds);
                RefreshMarksList();
            }
            _capture.Pause();
            _pausedAt = DateTime.Now;
            _pauseCount++;
        }
    }

    private void BeginStopConfirm()
    {
        _awaitingStopConfirm = true;
        ConfirmBannerText.Text = $"녹음을 종료할까요? {FormatClock(_capture.ElapsedSeconds)} 캡처됨 · Enter 확인, Esc 계속 녹음";
        ConfirmBanner.Visibility = Visibility.Visible;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        TogglePause();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (!_awaitingStopConfirm)
            BeginStopConfirm();
        else
            ConfirmStop();
    }

    private void CancelStopConfirm()
    {
        _awaitingStopConfirm = false;
        ConfirmBanner.Visibility = Visibility.Collapsed;
    }

    private void ConfirmStop()
    {
        _uiTimer.Stop();
        _capture.LevelChanged -= OnLevelChanged;

        _marking.AutoCloseAtStop(_capture.ElapsedSeconds);

        _session.AudioDurationSeconds = _capture.ElapsedSeconds;
        _session.Marks = _marking.Marks.ToList();
        _session.Gaps = _marking.ComputeGaps(_session.AudioDurationSeconds).ToList();
        _session.PausedTotalSeconds = _pausedTotalSeconds;
        _session.PauseCount = _pauseCount;
        _session.EndedAt = DateTimeOffset.Now;

        _capture.Stop();

        var markdown = MarkdownExporter.Build(_session);
        File.WriteAllText(_session.MarkdownPath, markdown);

        var summary = new ExportSummaryWindow(_session);
        summary.Show();

        Closing -= OnClosing;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Design guide: the recording UI never disappears mid-meeting and
        // Stop is the only exit. The full "minimise to mini bar" behaviour
        // is deferred (it needs a tray icon + global hotkeys, out of scope
        // for this pass) — for now the window close button routes through
        // the same confirm-and-stop flow as Esc instead of being blocked
        // outright.
        e.Cancel = true;
        if (!_awaitingStopConfirm) BeginStopConfirm();
    }
}
