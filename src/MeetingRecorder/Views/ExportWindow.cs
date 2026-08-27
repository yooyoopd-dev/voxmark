using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// S5 · Stop &amp; export — design guide section 08.
///
/// Audio was written continuously during the meeting, so nothing is being
/// re-encoded from scratch: this is a finalise pass. Unmarked time is
/// reported honestly rather than hidden, because it tells the operator how
/// much of the transcript will come back without a speaker attached.
/// </summary>
public sealed class ExportWindow : ShellWindow
{
    private readonly RecordingSession _session;

    private readonly TextBlock _audioStatus;
    private readonly TextBlock _markdownStatus;
    private readonly TextBlock _audioTick;
    private readonly TextBlock _markdownTick;
    private readonly Border _audioProgress;
    private readonly Border _markdownProgress;
    private readonly Border _audioTrack;
    private readonly Border _markdownTrack;
    private readonly StackPanel _summary = new();
    private readonly Button _copyButton;

    public ExportWindow(RecordingSession session, bool alreadyWritten)
        : base("Finishing — " + session.Title, 720, 760)
    {
        _session = session;
        MinWidth = 620;
        MinHeight = 620;

        _audioTick = Ui.Text("✓", 13, Palette.GoodBrush);
        _markdownTick = Ui.Text(alreadyWritten ? "✓" : "◐", 13,
            alreadyWritten ? Palette.GoodBrush : Palette.AccentBrush);

        _audioStatus = Ui.Mono(FileSize(session.Mp3Path), 12, Palette.TextMutedBrush);
        _markdownStatus = Ui.Mono(
            alreadyWritten ? FileSize(session.MarkdownPath) : "writing " + session.Marks.Count + " marks…",
            12, Palette.TextMutedBrush);

        _audioProgress = ProgressFill(Palette.GoodBrush, 1);
        _markdownProgress = ProgressFill(Palette.AccentBrush, alreadyWritten ? 1 : 0.1);
        _audioTrack = ProgressTrack(_audioProgress);
        _markdownTrack = ProgressTrack(_markdownProgress);

        _copyButton = Ui.MakeButton("Copy Markdown", null, "GhostButton", (_, _) => CopyMarkdown());
        _copyButton.MinHeight = 40;
        _copyButton.Margin = new Thickness(10, 0, 0, 0);

        SetBody(BuildBody(alreadyWritten));
        BuildSummary();

        Bar.CanMaximise = false;

        if (!alreadyWritten)
        {
            // Give the window one frame to paint before the write, so the
            // finalise pass reads as progress rather than as a freeze.
            Dispatcher.InvokeAsync(WriteMarkdown, DispatcherPriority.Background);
        }
    }

    private UIElement BuildBody(bool alreadyWritten)
    {
        var heading = Ui.Text("Recording stopped · " + Ui.Clock(_session.AudioDurationSeconds), 22);
        var blurb = Ui.Wrap(
            "Audio was written continuously during the meeting, so nothing is being re-encoded from scratch — " +
            "this is a finalise pass.", 13.5, Palette.TextDimBrush);
        blurb.Margin = new Thickness(0, 6, 0, 20);

        var audioCard = FileCard(_audioTick, Path.GetFileName(_session.Mp3Path), _audioStatus, _audioTrack);
        var markdownCard = FileCard(_markdownTick, Path.GetFileName(_session.MarkdownPath), _markdownStatus, _markdownTrack);
        audioCard.Margin = new Thickness(0, 0, 0, 10);

        var summaryCard = Ui.Card(Ui.Vertical(12,
            Ui.Section("Session summary"),
            _summary,
            Ui.Wrap("Unmarked time is reported honestly rather than hidden — it tells you how much of the " +
                    "transcript will come back without a speaker attached.", 12, Palette.TextMutedBrush)),
            new Thickness(15), Palette.HairlineSoftBrush, Palette.WellBrush);
        summaryCard.Margin = new Thickness(0, 20, 0, 0);

        var openFolder = Ui.MakeButton("Open folder", null, "GhostButton", (_, _) => OpenFolder());
        openFolder.MinHeight = 40;

        var done = Ui.MakeButton("Done", null, "AccentButton", (_, _) => Finish());

        var actions = Ui.Columns(2, openFolder, _copyButton, Ui.Filler(), done);
        actions.Margin = new Thickness(0, 20, 0, 0);

        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(blurb);
        content.Children.Add(audioCard);
        content.Children.Add(markdownCard);
        content.Children.Add(summaryCard);
        content.Children.Add(actions);

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24),
            Content = content,
        };
    }

    private static Border ProgressFill(System.Windows.Media.Brush brush, double fraction) => new()
    {
        Height = 5,
        Background = brush,
        HorizontalAlignment = HorizontalAlignment.Left,
        CornerRadius = new CornerRadius(3),
        Tag = fraction,
    };

    private static Border ProgressTrack(Border fill)
    {
        var track = new Border
        {
            Height = 5,
            Background = Palette.WellBrush,
            CornerRadius = new CornerRadius(3),
            Child = fill,
        };
        track.SizeChanged += (_, _) =>
        {
            var fraction = fill.Tag is double value ? value : 0;
            fill.Width = Math.Max(0, track.ActualWidth * fraction);
        };
        return track;
    }

    private static void SetProgress(Border track, Border fill, double fraction)
    {
        fill.Tag = fraction;
        fill.Width = Math.Max(0, track.ActualWidth * fraction);
    }

    private static Border FileCard(TextBlock tick, string fileName, TextBlock status, Border track)
    {
        var row = Ui.Columns(1,
            tick,
            Ui.Mono("  " + fileName, 13.5, Palette.TextBrush),
            status);
        row.Margin = new Thickness(0, 0, 0, 9);

        return Ui.Card(Ui.Vertical(0, row, track), new Thickness(15, 13, 15, 13));
    }

    private void WriteMarkdown()
    {
        try
        {
            // UTF-8, LF, no BOM — section 10.
            File.WriteAllText(_session.MarkdownPath, MarkdownExporter.Build(_session), new UTF8Encoding(false));
            _session.Completed = true;
            SessionStore.Save(_session);

            _markdownTick.Text = "✓";
            _markdownTick.Foreground = Palette.GoodBrush;
            _markdownStatus.Text = FileSize(_session.MarkdownPath);
            SetProgress(_markdownTrack, _markdownProgress, 1);
        }
        catch (Exception ex)
        {
            _markdownTick.Text = "✕";
            _markdownTick.Foreground = Palette.RecBrush;
            _markdownStatus.Text = ex.Message;
            _markdownStatus.Foreground = Palette.RecBrush;
        }
    }

    private void BuildSummary()
    {
        _summary.Children.Clear();

        var total = Math.Max(1.0, _session.AudioDurationSeconds);
        var rows = _session.Speakers
            .Select(speaker => (
                Name: speaker.Name,
                Slot: speaker.SlotIndex,
                Seconds: _session.Marks.Where(m => m.SpeakerSlot == speaker.SlotIndex).Sum(m => m.DurationSeconds)))
            .Where(row => row.Seconds > 0)
            .OrderByDescending(row => row.Seconds)
            .ToList();

        foreach (var row in rows)
        {
            _summary.Children.Add(SummaryRow(row.Name, Palette.BrushForSlot(row.Slot), row.Seconds, total));
        }

        var unmarked = _session.Gaps.Sum(g => g.Duration);
        if (unmarked > 0)
        {
            _summary.Children.Add(SummaryRow("Unmarked", Palette.TextFaintBrush, unmarked, total, muted: true));
        }

        if (_summary.Children.Count == 0)
        {
            _summary.Children.Add(Ui.Text("No marks were made in this session.", 13, Palette.TextMutedBrush));
        }
    }

    private static UIElement SummaryRow(string name, System.Windows.Media.Brush colour, double seconds,
                                        double total, bool muted = false)
    {
        var share = Math.Clamp(seconds / total, 0, 1);

        var swatch = new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = colour,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var label = Ui.Text(name, 13, muted ? Palette.TextDimBrush : Palette.TextBrush);
        label.Width = 120;

        var fill = new Border
        {
            Height = 8,
            Background = colour,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = share,
        };
        var track = new Border
        {
            Height = 8,
            Background = Palette.SurfaceBrush,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 10, 0),
            Child = fill,
        };
        track.SizeChanged += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * share);

        var stat = Ui.Mono(Ui.Short(seconds) + " · " + (share * 100).ToString("0") + "%", 12,
            muted ? Palette.TextMutedBrush : Palette.TextBodyBrush);
        stat.Width = 84;
        stat.TextAlignment = TextAlignment.Right;

        var row = Ui.Columns(2, swatch, label, track, stat);
        row.Margin = new Thickness(0, 0, 0, 8);
        return row;
    }

    private static string FileSize(string path)
    {
        try
        {
            if (!File.Exists(path)) return "not written";
            var bytes = new FileInfo(path).Length;
            return bytes >= 1024 * 1024
                ? (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB"
                : (bytes / 1024.0).ToString("0.0") + " KB";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void CopyMarkdown()
    {
        try
        {
            Clipboard.SetText(File.ReadAllText(_session.MarkdownPath));
            _copyButton.Content = "Copied";
        }
        catch (Exception)
        {
            _copyButton.Content = "Could not copy";
        }
    }

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _session.SessionFolder + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Never take the app down over a shell call.
        }
    }

    private void Finish()
    {
        var library = new LibraryWindow();
        library.Show();
        Close();
    }
}
