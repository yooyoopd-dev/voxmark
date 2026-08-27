using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeetingRecorder.Models;

namespace MeetingRecorder.Views;

public partial class ExportSummaryWindow : Window
{
    private static readonly SolidColorBrush WellBrush = new(Color.FromRgb(0x23, 0x25, 0x32));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0x91, 0x84, 0xd9));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0xb2, 0xb6, 0xca));

    private readonly RecordingSession _session;

    public ExportSummaryWindow(RecordingSession session)
    {
        InitializeComponent();
        _session = session;

        var duration = TimeSpan.FromSeconds(session.AudioDurationSeconds);
        HeadingText.Text = $"녹음 종료 · {(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        SubText.Text = $"{Path.GetFileName(session.Mp3Path)} + {Path.GetFileName(session.MarkdownPath)} 저장됨 — {session.SessionFolder}";

        BuildSummary();
    }

    private void BuildSummary()
    {
        SummaryPanel.Children.Clear();

        var talkSeconds = new Dictionary<int, double>();
        foreach (var mark in _session.Marks)
        {
            talkSeconds[mark.SpeakerSlot] = talkSeconds.GetValueOrDefault(mark.SpeakerSlot) + mark.DurationSeconds;
        }

        var total = Math.Max(1.0, _session.AudioDurationSeconds);

        foreach (var speaker in _session.Speakers)
        {
            var seconds = talkSeconds.GetValueOrDefault(speaker.SlotIndex);
            var percent = seconds / total * 100.0;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = speaker.Name, Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);

            var barHost = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            barHost.Children.Add(new Border { Background = WellBrush, Height = 8, CornerRadius = new CornerRadius(4) });
            barHost.Children.Add(new Border
            {
                Background = AccentBrush,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, percent) * 2.4,
            });
            Grid.SetColumn(barHost, 1);

            var talkSpan = TimeSpan.FromSeconds(seconds);
            var stat = new TextBlock
            {
                Text = $"{(int)talkSpan.TotalMinutes:00}:{talkSpan.Seconds:00} · {percent:0}%",
                Foreground = MutedBrush,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(stat, 2);

            row.Children.Add(name);
            row.Children.Add(barHost);
            row.Children.Add(stat);
            SummaryPanel.Children.Add(row);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start("explorer.exe", $"\"{_session.SessionFolder}\"");
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        new SetupWindow().Show();
        Close();
    }
}
