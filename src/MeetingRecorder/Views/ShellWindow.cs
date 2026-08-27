using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MeetingRecorder.Controls;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// Common shell for every screen: the app owns a flat dark ground, so the
/// system chrome is replaced by the 40px bar the design guide draws on all
/// five screens. Mica stays off deliberately — it would let the desktop
/// wallpaper through a screen the operator stares at for an hour.
/// </summary>
public abstract class ShellWindow : Window
{
    private readonly Border _bodyHost;

    protected ShellWindow(string title, double width, double height)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.WindowBrush;
        Foreground = Palette.TextBrush;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13.5;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        UseLayoutRounding = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        Bar = new TitleBar { TitleText = title };

        _bodyHost = new Border();

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(Bar, Dock.Top);
        root.Children.Add(Bar);
        root.Children.Add(_bodyHost);

        Content = root;

        // Every screen replaces the previous one, so the app's lifetime is
        // "is any screen still open?" rather than "did a window close?".
        Closed += (_, _) => Dispatcher.InvokeAsync(ShutdownIfLastScreen, DispatcherPriority.Background);
    }

    /// <summary>Quit once no screen is left; the toast window must not keep the process alive.</summary>
    private static void ShutdownIfLastScreen()
    {
        var app = Application.Current;
        if (app is null) return;

        foreach (var window in app.Windows)
        {
            if (window is ShellWindow shell && shell.IsVisible) return;
        }
        app.Shutdown();
    }

    protected TitleBar Bar { get; }

    protected void SetBody(UIElement body) => _bodyHost.Child = body;

    /// <summary>Update both the OS window title and the drawn title bar.</summary>
    protected void SetTitle(string title)
    {
        Title = title;
        Bar.TitleText = title;
    }
}
