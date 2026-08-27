using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Controls;

/// <summary>
/// The 40px window chrome every screen in the design guide is drawn with:
/// a slot-coloured app mark, the meeting title, and ─ ☐ ✕ on the right.
///
/// The app owns a flat dark ground, so the system chrome is replaced rather
/// than tinted. Close is disabled while recording — section 08: "the window
/// close button is disabled while recording; closing the window minimises to
/// the mini bar instead."
/// </summary>
public sealed class TitleBar : UserControl
{
    private readonly TextBlock _title = new()
    {
        FontSize = 12.5,
        Foreground = Palette.TextBodyBrush,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly Button _minimise;
    private readonly Button _maximise;
    private readonly Button _close;

    public TitleBar()
    {
        Height = 40;
        Background = Palette.ChromeBrush;

        _minimise = ChromeButton("─", "TitleBarButton");
        _maximise = ChromeButton("☐", "TitleBarButton");
        _close = ChromeButton("✕", "CloseBarButton");

        _minimise.Click += (_, _) =>
        {
            if (Host() is { } window) window.WindowState = WindowState.Minimized;
        };
        _maximise.Click += (_, _) =>
        {
            if (Host() is { } window) ToggleMaximise(window);
        };
        _close.Click += (_, _) => OnCloseRequested();

        var mark = new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 3,
            RadiusY = 3,
            Fill = Palette.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_minimise);
        buttons.Children.Add(_maximise);
        buttons.Children.Add(_close);

        var grid = new Grid { Margin = new Thickness(14, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(mark, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(mark);
        grid.Children.Add(_title);
        grid.Children.Add(buttons);

        var root = new Border
        {
            Background = Palette.ChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xE9, 0xE9, 0xED)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };

        // Dragging, double-click-to-maximise and Aero Snap are left to
        // WindowChrome's caption area (see each window's WindowChrome
        // CaptionHeight), which is why the buttons opt out of it above.
        Content = root;
    }

    /// <summary>Raised instead of closing when <see cref="CanClose"/> is false.</summary>
    public event Action? CloseBlocked;

    public string TitleText
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    private bool _canClose = true;

    public bool CanClose
    {
        get => _canClose;
        set
        {
            _canClose = value;
            _close.IsEnabled = value;
            _close.ToolTip = value ? null : "Stop the recording first — Esc";
        }
    }

    public bool CanMaximise
    {
        get => _maximise.Visibility == Visibility.Visible;
        set => _maximise.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseRequested()
    {
        if (!CanClose)
        {
            CloseBlocked?.Invoke();
            return;
        }
        Host()?.Close();
    }

    private static void ToggleMaximise(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private Window? Host() => Window.GetWindow(this);

    private Button ChromeButton(string glyph, string styleKey)
    {
        var button = new Button { Content = glyph };
        if (TryFindResource(styleKey) is Style style) button.Style = style;
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }
}
