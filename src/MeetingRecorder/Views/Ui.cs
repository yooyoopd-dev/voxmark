using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// Small builders for the shapes the design guide repeats on every screen:
/// the 11px uppercase section label, the surface card with a hairline edge,
/// the inset well, and buttons that carry their shortcut in mono beside the
/// label. Spacing follows the guide's 4px scale (4·8·12·16·24·32).
/// </summary>
internal static class Ui
{
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    public static TextBlock Text(string text, double size = 13.5, Brush? foreground = null,
                                 FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = UiFont,
            Foreground = foreground ?? Palette.TextBrush,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Wrap(string text, double size = 13.5, Brush? foreground = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = UiFont,
            Foreground = foreground ?? Palette.TextBodyBrush,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>11px uppercase label — the guide's standard section header.</summary>
    public static TextBlock Section(string text, Brush? foreground = null)
    {
        return new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11,
            FontFamily = UiFont,
            Foreground = foreground ?? Palette.TextDimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Mono(string text, double size = 12.5, Brush? foreground = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = MonoFont,
            Foreground = foreground ?? Palette.TextBodyBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Surface + 1px hairline + radius 8. Never a shadow.</summary>
    public static Border Card(UIElement child, Thickness? padding = null, Brush? border = null,
                              Brush? background = null, double radius = 8)
    {
        return new Border
        {
            Background = background ?? Palette.SurfaceBrush,
            BorderBrush = border ?? Palette.HairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(16),
            Child = child,
            SnapsToDevicePixels = true,
        };
    }

    /// <summary>The darker inset well used behind waveforms, lanes and meters.</summary>
    public static Border Well(UIElement? child, Thickness? padding = null, double radius = 8)
    {
        return new Border
        {
            Background = Palette.WellBrush,
            BorderBrush = Palette.HairlineSoftBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(0),
            Child = child,
            SnapsToDevicePixels = true,
        };
    }

    public static StackPanel Horizontal(double gap, params UIElement[] children)
        => Stack(Orientation.Horizontal, gap, children);

    public static StackPanel Vertical(double gap, params UIElement[] children)
        => Stack(Orientation.Vertical, gap, children);

    private static StackPanel Stack(Orientation orientation, double gap, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        for (var i = 0; i < children.Length; i++)
        {
            if (gap > 0 && children[i] is FrameworkElement element && i < children.Length - 1)
            {
                element.Margin = orientation == Orientation.Horizontal
                    ? new Thickness(0, 0, gap, 0)
                    : new Thickness(0, 0, 0, gap);
            }
            panel.Children.Add(children[i]);
        }
        return panel;
    }

    /// <summary>A grid row where the given columns are Auto and the marked one takes the slack.</summary>
    public static Grid Columns(int starColumn, params UIElement[] children)
    {
        var grid = new Grid();
        for (var i = 0; i < children.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == starColumn ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    /// <summary>A button whose label carries its shortcut in mono, as every guide mockup does.</summary>
    public static Button MakeButton(string label, string? shortcut, string styleKey, RoutedEventHandler? onClick = null)
    {
        var button = new Button();
        if (Application.Current?.TryFindResource(styleKey) is Style style) button.Style = style;

        if (string.IsNullOrEmpty(shortcut))
        {
            button.Content = label;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = UiFont,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = shortcut,
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = Palette.TextMutedBrush,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            button.Content = row;
        }

        if (onClick is not null) button.Click += onClick;
        return button;
    }

    public static FrameworkElement Filler() => new Border { Background = Brushes.Transparent };

    /// <summary>A 1px separator on the guide's low-contrast rule colour.</summary>
    public static Border Rule(double top = 0, double bottom = 0)
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xE9, 0xE9, 0xED)),
            Margin = new Thickness(0, top, 0, bottom),
        };
    }

    public static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" +
               span.Seconds.ToString("00");
    }

    /// <summary>MM:SS, for talk-time readouts that never reach an hour on a tile.</summary>
    public static string Short(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalMinutes).ToString("00") + ":" + span.Seconds.ToString("00");
    }

    /// <summary>HH:MM:SS.t — the dock's timecode precision.</summary>
    public static string Tenths(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" +
               span.Seconds.ToString("00") + "." + (span.Milliseconds / 100).ToString("0");
    }
}
