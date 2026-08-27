using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Controls;

/// <summary>
/// A dark drop-down built from a button and a popup rather than a styled
/// <c>ComboBox</c>. Fluent's colours are replaced wholesale by the tokens in
/// section 02, and re-templating a ComboBox to get there leaves more system
/// chrome behind than it removes.
///
/// Used for the input device picker on setup and for the inline "reassign ▾"
/// in the marks dock — which section 06 requires to be inline, never a modal.
/// </summary>
public sealed class Dropdown : Button
{
    private readonly TextBlock _label;
    private readonly Popup _popup;
    private readonly StackPanel _list;
    private readonly List<(string Label, object Value)> _items = new();

    public Dropdown(string styleKey = "GhostButton")
    {
        if (TryFindResource(styleKey) is Style style) Style = style;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;

        _label = new TextBlock
        {
            Foreground = Palette.TextBodyBrush,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var caret = new TextBlock
        {
            Text = "▾",
            FontSize = 11,
            Foreground = Palette.AccentTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(caret, 1);
        row.Children.Add(_label);
        row.Children.Add(caret);
        Content = row;

        _list = new StackPanel();
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = Palette.SurfaceBrush,
                BorderBrush = Palette.HairlineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 4, 0, 0),
                Child = new ScrollViewer
                {
                    MaxHeight = 320,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _list,
                },
            },
        };

        Click += (_, _) => Toggle();
    }

    public object? SelectedValue { get; private set; }

    public event Action<object>? SelectionChanged;

    public string DisplayText
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    /// <summary>Match the popup width to the button, so long device names stay readable.</summary>
    public double PopupMinWidth { get; set; }

    public void SetItems(IEnumerable<(string Label, object Value)> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Rebuild();
    }

    public void Select(object value)
    {
        foreach (var item in _items)
        {
            if (Equals(item.Value, value))
            {
                SelectedValue = value;
                _label.Text = item.Label;
                return;
            }
        }
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        foreach (var item in _items)
        {
            var entry = new Button
            {
                Content = new TextBlock
                {
                    Text = item.Label,
                    Foreground = Palette.TextSecondaryBrush,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 32,
                Padding = new Thickness(10, 0, 10, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            if (TryFindResource("GhostButton") is Style style) entry.Style = style;
            entry.BorderThickness = new Thickness(0);
            entry.HorizontalContentAlignment = HorizontalAlignment.Left;

            var captured = item;
            entry.Click += (_, _) =>
            {
                _popup.IsOpen = false;
                SelectedValue = captured.Value;
                _label.Text = captured.Label;
                SelectionChanged?.Invoke(captured.Value);
            };
            _list.Children.Add(entry);
        }
    }

    private void Toggle()
    {
        if (_items.Count == 0) return;
        _list.MinWidth = PopupMinWidth > 0 ? PopupMinWidth : ActualWidth;
        _popup.IsOpen = !_popup.IsOpen;
    }
}
