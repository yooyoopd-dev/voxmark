using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeetingRecorder.Models;
using MeetingRecorder.Theme;
using MeetingRecorder.Views;

namespace MeetingRecorder.Controls;

/// <summary>
/// One row of the setup roster — design guide section 07: a slot chip in the
/// slot's colour, editable name and role, the speaker's own key cell, and a
/// drag handle.
///
/// Section 06 is specific about the key cell: keys live on the preset, two
/// speakers can never hold the same key, and an already-taken combination is
/// rejected at keystroke time rather than accepted and warned about later.
/// </summary>
public sealed class RosterRow : Border
{
    private readonly TextBox _name;
    private readonly TextBox _role;
    private readonly Button _keyCell;
    private readonly Border _slotChip;
    private readonly TextBlock _slotNumber;
    private readonly Button _absent;

    public RosterRow(Speaker speaker)
    {
        Speaker = speaker;

        Background = Palette.SurfaceBrush;
        BorderBrush = Palette.HairlineBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(11, 9, 11, 9);
        Margin = new Thickness(0, 0, 0, 6);
        SnapsToDevicePixels = true;

        _slotNumber = new TextBlock
        {
            FontFamily = Ui.MonoFont,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _slotChip = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = _slotNumber,
        };

        _name = new TextBox
        {
            Text = speaker.Name,
            FontSize = 14.5,
            Foreground = Palette.TextBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        _name.TextChanged += (_, _) =>
        {
            Speaker.Name = _name.Text;
            Changed?.Invoke();
        };

        _role = new TextBox
        {
            Text = speaker.Role,
            FontSize = 11,
            Foreground = Palette.TextMutedBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        _role.TextChanged += (_, _) =>
        {
            Speaker.Role = _role.Text;
            Changed?.Invoke();
        };

        var identity = new StackPanel();
        identity.Children.Add(_name);
        identity.Children.Add(_role);

        _keyCell = new Button { MinHeight = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(10, 0, 8, 0) };
        if (Application.Current?.TryFindResource("ChipButtonAccent") is Style chip) _keyCell.Style = chip;
        _keyCell.Click += (_, _) => KeyCaptureRequested?.Invoke(this);

        _absent = new Button { MinHeight = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current?.TryFindResource("ChipButton") is Style plain) _absent.Style = plain;
        _absent.Click += (_, _) =>
        {
            Speaker.IsAbsent = !Speaker.IsAbsent;
            Refresh();
            Changed?.Invoke();
        };

        var handle = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 13,
            Foreground = Palette.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.SizeNS,
            ToolTip = "Drag to reorder",
        };
        handle.MouseLeftButtonDown += (_, _) => DragRequested?.Invoke(this);

        var remove = new Button { Content = "✕", MinHeight = 26, Width = 28, Padding = new Thickness(0) };
        if (Application.Current?.TryFindResource("ChipButton") is Style removeStyle) remove.Style = removeStyle;
        remove.Foreground = Palette.TextMutedBrush;
        remove.BorderThickness = new Thickness(0);
        remove.Click += (_, _) => RemoveRequested?.Invoke(this);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_slotChip, 0);
        Grid.SetColumn(identity, 1);
        Grid.SetColumn(_absent, 2);
        Grid.SetColumn(_keyCell, 3);
        Grid.SetColumn(handle, 4);
        Grid.SetColumn(remove, 5);
        grid.Children.Add(_slotChip);
        grid.Children.Add(identity);
        grid.Children.Add(_absent);
        grid.Children.Add(_keyCell);
        grid.Children.Add(handle);
        grid.Children.Add(remove);

        Child = grid;
        Refresh();
    }

    public Speaker Speaker { get; }

    public event Action<RosterRow>? RemoveRequested;
    public event Action<RosterRow>? KeyCaptureRequested;
    public event Action<RosterRow>? DragRequested;
    public event Action? Changed;

    /// <summary>Re-read the speaker: slot colour, key label, absent state.</summary>
    public void Refresh()
    {
        var colour = Palette.ForSlot(Speaker.SlotIndex);
        _slotChip.Background = Palette.TintBrush(colour, 0.2);
        _slotChip.BorderBrush = new SolidColorBrush(colour);
        _slotNumber.Text = (Speaker.SlotIndex + 1).ToString();
        _slotNumber.Foreground = new SolidColorBrush(Palette.Tint(colour, 1.0));

        _keyCell.Content = new TextBlock
        {
            Text = Speaker.Key.GlobalLabel,
            FontFamily = Ui.MonoFont,
            FontSize = 11.5,
            Foreground = Speaker.IsAbsent ? Palette.TextMutedBrush : Palette.AccentTextStrongBrush,
        };
        _keyCell.ToolTip = "Marking key " + Speaker.KeyLabel + " · click, then press the new key";

        _absent.Content = Speaker.IsAbsent ? "Absent" : "Present";
        _absent.Foreground = Speaker.IsAbsent ? Palette.WarnBrush : Palette.TextMutedBrush;

        // Section 07: an absent speaker's tile stays dimmed and their key
        // stays reserved — the row is not removed.
        Opacity = Speaker.IsAbsent ? 0.55 : 1.0;
        _role.Text = Speaker.Role;
    }

    /// <summary>Highlight the key cell while it waits for a keystroke.</summary>
    public void SetCapturing(bool capturing)
    {
        if (capturing)
        {
            _keyCell.Content = new TextBlock
            {
                Text = "press 1–9, 0, ⇧1, ⇧2",
                FontFamily = Ui.MonoFont,
                FontSize = 11,
                Foreground = Palette.AccentTextStrongBrush,
            };
            _keyCell.BorderBrush = Palette.AccentBrush;
        }
        else
        {
            _keyCell.BorderBrush = Palette.AccentEdgeBrush;
            Refresh();
        }
    }

    /// <summary>Flash the cell when the operator picks a key another row already holds.</summary>
    public void RejectKey()
    {
        _keyCell.BorderBrush = Palette.RecBrush;
        _keyCell.Content = new TextBlock
        {
            Text = "already taken",
            FontFamily = Ui.MonoFont,
            FontSize = 11,
            Foreground = Palette.RecTextBrush,
        };
    }

    public void FocusName()
    {
        _name.Focus();
        _name.SelectAll();
    }
}
