using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeetingRecorder.Models;
using MeetingRecorder.Services;

namespace MeetingRecorder.Views;

public partial class SetupWindow : Window
{
    private readonly ObservableCollection<Speaker> _roster = new();
    private readonly InputLevelMeter _meter = new();
    private readonly List<(int Id, string Name)> _devices = new();

    public SetupWindow()
    {
        InitializeComponent();
        RosterList.ItemsSource = _roster;

        _devices.AddRange(AudioCaptureService.GetInputDevices());
        foreach (var device in _devices)
        {
            DeviceCombo.Items.Add(device.Name);
        }
        if (DeviceCombo.Items.Count > 1)
        {
            DeviceCombo.SelectedIndex = 1;
        }
        else if (DeviceCombo.Items.Count > 0)
        {
            DeviceCombo.SelectedIndex = 0;
        }

        DeviceCombo.SelectionChanged += (_, _) => RestartMeter();
        Loaded += (_, _) => RestartMeter();
        Closed += (_, _) => _meter.Dispose();

        _meter.LevelChanged += level => Dispatcher.BeginInvoke(() =>
        {
            var track = (Grid)LevelBar.Parent;
            LevelBar.Width = Math.Max(0, Math.Min(1, level)) * track.ActualWidth;
        });
    }

    private void RestartMeter()
    {
        if (DeviceCombo.SelectedIndex < 0) return;
        try
        {
            _meter.Start(_devices[DeviceCombo.SelectedIndex].Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"입력 장치를 열 수 없습니다: {ex.Message}", "오류");
        }
    }

    private void OnAddSpeaker(object sender, RoutedEventArgs e)
    {
        var name = NewSpeakerName.Text.Trim();
        if (name.Length == 0 || _roster.Count >= 12) return;

        _roster.Add(new Speaker { Name = name, Role = NewSpeakerRole.Text.Trim() });
        NewSpeakerName.Clear();
        NewSpeakerRole.Clear();
        NewSpeakerName.Focus();
    }

    private void OnRemoveSpeakerClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Speaker speaker })
        {
            _roster.Remove(speaker);
        }
    }

    private void OnStartRecording(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedIndex < 0)
        {
            MessageBox.Show(this, "먼저 입력 장치를 선택하세요.", "입력 장치 없음");
            return;
        }
        if (_roster.Count == 0)
        {
            MessageBox.Show(this, "최소 한 명 이상의 발화자를 등록하세요.", "발화자 없음");
            return;
        }

        _meter.Dispose();

        // Slots (and therefore palette colours, marking keys, and Markdown
        // speaker ids) are assigned now, by final roster order.
        for (var i = 0; i < _roster.Count; i++)
        {
            _roster[i].SlotIndex = i;
        }

        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "New Meeting" : TitleBox.Text.Trim();
        var slug = Slugify(title);
        var dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "VoxMark", "Sessions", $"{dateStamp}_{slug}");
        Directory.CreateDirectory(folder);

        var session = new RecordingSession
        {
            Title = title,
            SessionFolder = folder,
            AudioFileName = $"{slug}_{dateStamp}.mp3",
            Speakers = _roster.ToList(),
        };

        var deviceNumber = _devices[DeviceCombo.SelectedIndex].Id;
        var recordingWindow = new RecordingWindow(session, deviceNumber);
        recordingWindow.Show();
        Close();
    }

    private static string Slugify(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length > 0 ? slug : "meeting";
    }
}
