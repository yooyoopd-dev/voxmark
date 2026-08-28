using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MeetingRecorder.Models;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Controls;

/// <summary>Tile density bands from design guide section 05, "auto-shrinking grid · 2 → 12".</summary>
public enum TileDensity
{
    /// <summary>2–4 speakers · 2 cols · 148px · name 24px · talk-time bar.</summary>
    Roomy,

    /// <summary>5–6 · 3 cols · 116px · name 19px. The default.</summary>
    Default,

    /// <summary>7–9 · 3 cols · 96px · name 17px · role drops to a coloured dot.</summary>
    Tight,

    /// <summary>10–12 · 4 cols · 76px · name 16px · name and time only.</summary>
    Dense,
}

/// <summary>
/// One speaker tile. Column count and tile height are derived from the
/// speaker count alone and the user cannot set them; tile order is roster
/// order and is fixed for the whole session, because sorting by talk time
/// would defeat muscle memory (section 05).
/// </summary>
public sealed class SpeakerTile : Border
{
    private readonly Speaker _speaker;
    private readonly TileDensity _density;
    private readonly Color _color;

    private readonly TextBlock _name;
    private readonly TextBlock _key;
    private readonly TextBlock _meta;
    private readonly TextBlock _markTime;
    private readonly TextBlock? _role;
    private readonly Ellipse? _roleDot;
    private readonly Border? _talkTrack;
    private readonly Border? _talkFill;
    private readonly Border _colourBar;

    private bool _isOpen;

    public SpeakerTile(Speaker speaker, TileDensity density, bool compact = false)
    {
        _speaker = speaker;
        _density = density;
        _color = Palette.ForSlot(speaker.SlotIndex);

        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = Palette.HairlineBrush;
        Background = Palette.SurfaceBrush;
        Cursor = Cursors.Hand;
        Height = HeightFor(density, compact);
        // Section 02: tiles never fall below 72px tall or 160px wide.
        MinWidth = 160;
        MinHeight = 72;
        SnapsToDevicePixels = true;

        _colourBar = new Border
        {
            Width = 3,
            Background = new SolidColorBrush(_color),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _key = new TextBlock
        {
            Text = speaker.KeyLabel,
            FontFamily = Mono,
            FontSize = 11,
            Foreground = Palette.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _name = new TextBlock
        {
            Text = speaker.Name,
            FontFamily = Ui,
            FontSize = NameSizeFor(density),
            FontWeight = FontWeights.Medium,
            Foreground = Palette.TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _meta = new TextBlock
        {
            Text = "—",
            FontFamily = Mono,
            FontSize = 12.5,
            Foreground = Palette.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // The open mark's start, shown on the tile itself so the operator can
        // read and correct it (← →) without looking away from the grid.
        _markTime = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 11,
            Foreground = Palette.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_key);
        top.Children.Add(_name);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (density == TileDensity.Tight)
        {
            // Role text drops to a coloured dot; cumulative time stays.
            _roleDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(_color),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_roleDot, 0);
            bottom.Children.Add(_roleDot);
        }
        else if (density != TileDensity.Dense)
        {
            _role = new TextBlock
            {
                Text = speaker.Role,
                FontFamily = Ui,
                FontSize = 12,
                Foreground = Palette.TextMutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_role, 0);
            bottom.Children.Add(_role);
        }

        if (density == TileDensity.Roomy)
        {
            // Room to spare, so each tile also carries a small talk-time bar.
            _talkTrack = new Border
            {
                Width = 74,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Palette.WellBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            _talkFill = new Border
            {
                Height = 6,
                Width = 0,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(_color),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _talkTrack.Child = _talkFill;

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(_talkTrack);
            right.Children.Add(_meta);
            Grid.SetColumn(right, 1);
            bottom.Children.Add(right);
        }
        else
        {
            Grid.SetColumn(_meta, 1);
            bottom.Children.Add(_meta);
        }

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(top, 0);
        Grid.SetRow(_markTime, 1);
        Grid.SetRow(bottom, 2);
        stack.Children.Add(top);
        stack.Children.Add(_markTime);
        stack.Children.Add(bottom);
        stack.Margin = new Thickness(14, 12, 14, 12);

        var root = new Grid();
        root.Children.Add(stack);
        root.Children.Add(_colourBar);

        Child = root;

        if (speaker.IsAbsent) Opacity = 0.45;

        MouseLeftButtonUp += (_, _) => Tapped?.Invoke(_speaker.SlotIndex);
        MouseEnter += (_, _) => { if (!_isOpen) BorderBrush = Palette.AccentEdgeBrush; };
        MouseLeave += (_, _) => { if (!_isOpen) BorderBrush = Palette.HairlineBrush; };
    }

    public event Action<int>? Tapped;

    public int SlotIndex => _speaker.SlotIndex;

    public void SetKeyLabel(string label) => _key.Text = label;

    public void SetName(string name) => _name.Text = name;

    /// <summary>
    /// Refresh the live state. <paramref name="share"/> is 0..1 of the
    /// session so far, used by the roomy layout's talk-time bar.
    /// </summary>
    public void Update(bool isOpen, double openSeconds, double talkSeconds, double share,
                       double markStartSeconds)
    {
        if (isOpen != _isOpen)
        {
            _isOpen = isOpen;
            if (isOpen)
            {
                Background = Palette.TintBrush(_color, 0.16);
                BorderBrush = new SolidColorBrush(_color);
                BorderThickness = new Thickness(1.5);
                Effect = new DropShadowEffect
                {
                    Color = _color,
                    BlurRadius = 22,
                    ShadowDepth = 0,
                    Opacity = 0.45,
                };
                _name.Foreground = Palette.TextBrush;
                _key.Foreground = Palette.TextSecondaryBrush;
            }
            else
            {
                Background = Palette.SurfaceBrush;
                BorderBrush = Palette.HairlineBrush;
                BorderThickness = new Thickness(1);
                Effect = null;
                _name.Foreground = _speaker.IsAbsent ? Palette.TextDimBrush : Palette.TextBrush;
                _key.Foreground = Palette.TextMutedBrush;
            }
        }

        if (isOpen)
        {
            _meta.Text = "◉ " + Clock(openSeconds);
            _meta.Foreground = Palette.TextSecondaryBrush;
            _markTime.Text = "▶ " + Tenths(markStartSeconds);
            _markTime.Foreground = new SolidColorBrush(_color);
            _markTime.Visibility = Visibility.Visible;
        }
        else
        {
            _meta.Text = talkSeconds > 0 ? Clock(talkSeconds) : "—";
            _meta.Foreground = Palette.TextMutedBrush;
            _markTime.Visibility = Visibility.Collapsed;
        }

        if (_talkTrack is not null && _talkFill is not null)
        {
            _talkFill.Width = Math.Max(0, Math.Min(1, share)) * _talkTrack.Width;
        }
    }

    /// <summary>HH:MM:SS.t — tenths, because that is the nudge step.</summary>
    private static string Tenths(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" +
               span.Seconds.ToString("00") + "." + (span.Milliseconds / 100).ToString("0");
    }

    private static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalMinutes).ToString("00") + ":" + span.Seconds.ToString("00");
    }

    // ------------------------------------------------------------ grid rules

    /// <summary>Derived from speaker count alone — the user cannot set it.</summary>
    public static TileDensity DensityFor(int count) => count switch
    {
        <= 4 => TileDensity.Roomy,
        <= 6 => TileDensity.Default,
        <= 9 => TileDensity.Tight,
        _ => TileDensity.Dense,
    };

    /// <summary>Widening the window widens tiles; it never adds columns.</summary>
    public static int ColumnsFor(int count) => count switch
    {
        <= 4 => 2,
        <= 9 => 3,
        _ => 4,
    };

    /// <summary>
    /// Tile height for a density band. <paramref name="compact"/> is the
    /// recording screen giving up roughly a line of tile height to the live
    /// transcript strip — the grid stays the same shape, the tiles just lose
    /// slack. Section 02's 72px floor is never crossed, so even the densest
    /// compact tile is still a comfortable target in a dim room.
    /// </summary>
    public static double HeightFor(TileDensity density, bool compact = false)
    {
        var height = density switch
        {
            TileDensity.Roomy => 148.0,
            TileDensity.Default => 116.0,
            TileDensity.Tight => 96.0,
            _ => 76.0,
        };

        return compact ? Math.Max(72, height - CompactReductionFor(density)) : height;
    }

    /// <summary>
    /// Roomy tiles have the most slack to give and the dense ones almost
    /// none, so the reduction tapers rather than being flat.
    /// </summary>
    private static double CompactReductionFor(TileDensity density) => density switch
    {
        TileDensity.Roomy => 16,
        TileDensity.Default => 12,
        TileDensity.Tight => 8,
        _ => 4,
    };

    private static double NameSizeFor(TileDensity density) => density switch
    {
        TileDensity.Roomy => 24,
        TileDensity.Default => 19,
        TileDensity.Tight => 17,
        _ => 16,
    };

    private static FontFamily Ui { get; } = new("Segoe UI Variable Text, Segoe UI");
    private static FontFamily Mono { get; } = new("Cascadia Mono, Consolas");
}
