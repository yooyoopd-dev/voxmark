using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// The mini bar from design guide section 07. It is not a separate window in
/// the product sense and not persistent — it is the confirmation toast for a
/// global hotkey press while the app is in the background.
///
/// The rules it has to satisfy, verbatim from the guide: it appears at the
/// bottom-right of the primary display for 2 seconds and fades; it states
/// both halves of what just happened — who opened, and who was closed with
/// the duration they got, because the closing half is what lets the operator
/// catch a mis-press without opening the app; it must render in under 100 ms
/// (so the window is built once and reused), never steal focus, and never
/// queue — a second press replaces the toast rather than stacking. Clicking
/// it raises the full window.
/// </summary>
public sealed class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly TextBlock _headline = new()
    {
        FontSize = 15,
        Foreground = Palette.RecTextBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _detail = new()
    {
        FontSize = 12.5,
        Foreground = Palette.TextDimBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _clock = new()
    {
        FontSize = 20,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        Foreground = Palette.TextBodyBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal };
    private readonly Border _progress;
    private readonly Border _progressTrack;
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _hasBeenShown;

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 340;
        Focusable = false;
        Opacity = 0;
        Visibility = Visibility.Hidden;

        _progressTrack = new Border { Height = 4, Background = Palette.WellBrush };
        _progress = new Border
        {
            Height = 4,
            Background = Palette.RecBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Width,
        };
        _progressTrack.Child = _progress;

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Palette.RecBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Effect = new DropShadowEffect { Color = Palette.Rec, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_headline);
        text.Children.Add(_detail);

        var top = new Grid { Margin = new Thickness(14, 12, 14, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_clock, 2);
        top.Children.Add(dot);
        top.Children.Add(text);
        top.Children.Add(_clock);

        var expand = new TextBlock
        {
            Text = "⤢",
            FontSize = 12,
            Foreground = Palette.AccentTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var bottom = new Grid { Margin = new Thickness(14, 0, 14, 12) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_keys, 0);
        Grid.SetColumn(expand, 1);
        bottom.Children.Add(_keys);
        bottom.Children.Add(expand);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bottom);
        stack.Children.Add(_progressTrack);

        Content = new Border
        {
            Background = Palette.ChromeBrush,
            BorderBrush = Palette.AccentEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            // The one real shadow in the app, per section 02.
            Effect = new DropShadowEffect { BlurRadius = 44, ShadowDepth = 6, Opacity = 0.6, Color = Colors.Black },
            Margin = new Thickness(12),
            Child = stack,
        };

        _dismiss.Tick += (_, _) => BeginFadeOut();
        MouseLeftButtonUp += (_, _) => RaiseRequested?.Invoke();
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    /// <summary>Clicking the toast raises the full window.</summary>
    public event Action? RaiseRequested;

    private void MakeNonActivating()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>Rebuild the key chip row whenever the roster changes.</summary>
    public void SetRoster(IReadOnlyList<Speaker> speakers)
    {
        _keys.Children.Clear();
        foreach (var speaker in speakers)
        {
            _keys.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(5),
                Background = Palette.SurfaceBrush,
                BorderBrush = Palette.HairlineBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 5, 0),
                Tag = speaker.SlotIndex,
                Child = new TextBlock
                {
                    Text = speaker.KeyLabel,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    Foreground = Palette.TextBodyBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    /// <summary>
    /// Show what a hotkey press just did. A second press replaces the toast
    /// rather than stacking, so this always restarts the same window.
    /// </summary>
    public void ShowMark(RecordingSession session, MarkToggleResult result, double elapsedSeconds)
    {
        var opened = result.OpenedSlot is int openSlot ? session.SpeakerForSlot(openSlot) : null;
        var closed = result.ClosedSlot is int closeSlot ? session.SpeakerForSlot(closeSlot) : null;

        if (opened is not null)
        {
            _headline.Text = opened.Name + "  opened";
            _headline.Foreground = Palette.RecTextBrush;
        }
        else if (closed is not null)
        {
            _headline.Text = closed.Name + "  closed";
            _headline.Foreground = Palette.TextBrush;
        }
        else
        {
            _headline.Text = "Nothing to mark";
            _headline.Foreground = Palette.TextDimBrush;
        }

        _detail.Text = closed is null
            ? "Nothing was open before this"
            : closed.Name + " closed at " + MarkdownExporter.Timestamp(result.ClosedAt) +
              " · " + result.ClosedDuration.ToString("0.0") + " s";

        var span = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        _clock.Text = ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" +
                      span.Seconds.ToString("00");

        HighlightKey(result.OpenedSlot);

        _dismiss.Stop();
        if (!_hasBeenShown)
        {
            _hasBeenShown = true;
            Show();
        }
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Reposition();

        _progress.BeginAnimation(WidthProperty, null);
        _progress.Width = Width;
        _progress.BeginAnimation(WidthProperty, new DoubleAnimation(Width, 0, TimeSpan.FromSeconds(2)));
        _dismiss.Start();
    }

    private void HighlightKey(int? slot)
    {
        foreach (var child in _keys.Children)
        {
            if (child is not Border chip) continue;
            var isActive = chip.Tag is int tag && slot is int active && tag == active;
            chip.Background = isActive ? Palette.TintBrush(Palette.Rec, 0.18) : Palette.SurfaceBrush;
            chip.BorderBrush = isActive ? Palette.RecBrush : Palette.HairlineBrush;
            chip.BorderThickness = new Thickness(isActive ? 1.5 : 1);
            if (chip.Child is TextBlock label)
            {
                label.Foreground = isActive ? Palette.RecTextBrush : Palette.TextBodyBrush;
            }
        }
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        var height = ActualHeight > 0 ? ActualHeight : 128;
        Left = area.Right - Width - 12;
        Top = area.Bottom - height - 12;
    }

    private void BeginFadeOut()
    {
        _dismiss.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => Visibility = Visibility.Hidden;
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Hide immediately, e.g. when the main window comes back to the front.</summary>
    public void HideNow()
    {
        _dismiss.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Hidden;
    }
}
