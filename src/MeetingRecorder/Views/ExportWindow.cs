using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
///
/// A session split into several MP3s lists every file it produced, each with
/// the Markdown that goes with it.
/// </summary>
public sealed class ExportWindow : ShellWindow
{
    private readonly RecordingSession _session;
    private readonly List<AudioPart> _parts;
    private readonly StackPanel _files = new();
    private readonly StackPanel _summary = new();
    private readonly TextBlock _filesHeading;
    private readonly Button _copyButton;

    public ExportWindow(RecordingSession session, bool alreadyWritten)
        : base("Finishing — " + session.Title, 760, 800)
    {
        _session = session;
        _parts = session.EffectiveParts();
        MinWidth = 620;
        MinHeight = 620;

        _filesHeading = Ui.Section("Files");
        _copyButton = Ui.MakeButton("Copy Markdown", null, "GhostButton", (_, _) => CopyMarkdown());
        _copyButton.MinHeight = 40;
        _copyButton.Margin = new Thickness(10, 0, 0, 0);

        SetBody(BuildBody());
        BuildSummary();
        RefreshFiles(alreadyWritten ? "written" : "pending");

        Bar.CanMaximise = false;

        if (!alreadyWritten)
        {
            // Give the window one frame to paint before the write, so the
            // finalise pass reads as progress rather than as a freeze.
            Dispatcher.InvokeAsync(WriteMarkdown, DispatcherPriority.Background);
        }
    }

    private UIElement BuildBody()
    {
        var heading = Ui.Text("Recording stopped · " + Ui.Clock(_session.AudioDurationSeconds), 22);

        var blurb = Ui.Wrap(
            "Audio was written continuously during the meeting, so nothing is being re-encoded from scratch — " +
            "this is a finalise pass." +
            (_parts.Count > 1
                ? " The recording was split into " + _parts.Count +
                  " files; each one gets its own Markdown, and the timestamps keep counting from the first file."
                : ""),
            13.5, Palette.TextDimBrush);
        blurb.Margin = new Thickness(0, 6, 0, 20);

        var filesCard = Ui.Card(Ui.Vertical(12, _filesHeading, _files), new Thickness(15));

        var summaryCard = Ui.Card(Ui.Vertical(12,
            Ui.Section("Session summary"),
            _summary,
            Ui.Wrap("Unmarked time is reported honestly rather than hidden — it tells you how much of the " +
                    "transcript will come back without a speaker attached.", 12, Palette.TextMutedBrush)),
            new Thickness(15), Palette.HairlineSoftBrush, Palette.WellBrush);
        summaryCard.Margin = new Thickness(0, 12, 0, 0);

        var openFolder = Ui.MakeButton("Open folder", null, "GhostButton", (_, _) => OpenFolder());
        openFolder.MinHeight = 40;

        var done = Ui.MakeButton("Done", null, "AccentButton", (_, _) => Finish());

        var actions = Ui.Columns(2, openFolder, _copyButton, Ui.Filler(), done);
        actions.Margin = new Thickness(0, 20, 0, 0);

        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(blurb);
        content.Children.Add(filesCard);
        content.Children.Add(summaryCard);
        content.Children.Add(actions);

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24),
            Content = content,
        };
    }

    /// <summary>Redraw the file list. <paramref name="markdownState"/> is "pending", "written" or an error.</summary>
    private void RefreshFiles(string markdownState)
    {
        _files.Children.Clear();
        _filesHeading.Text = ("Files · " + (_parts.Count * 2)).ToUpperInvariant();

        foreach (var part in _parts)
        {
            var audioPath = _session.PathOf(part);
            _files.Children.Add(FileRow("✓", Palette.GoodBrush, part.FileName, FileSize(audioPath)));

            var markdownPath = _session.MarkdownPathOf(part);
            var written = File.Exists(markdownPath);
            var glyph = markdownState == "written" || written ? "✓" : markdownState == "pending" ? "◐" : "✕";
            var brush = markdownState == "written" || written
                ? Palette.GoodBrush
                : markdownState == "pending" ? Palette.AccentBrush : Palette.RecBrush;
            var detail = written ? FileSize(markdownPath)
                : markdownState == "pending" ? "writing…" : markdownState;

            _files.Children.Add(FileRow(glyph, brush, Path.GetFileName(markdownPath), detail));
        }

        if (_parts.Count > 1)
        {
            _files.Children.Add(Ui.Wrap(
                "Every Markdown carries audio_part_start — subtract it from a timestamp to seek inside that file.",
                11.5, Palette.TextMutedBrush));
        }
    }

    private static UIElement FileRow(string glyph, Brush glyphBrush, string name, string detail)
    {
        var tick = Ui.Text(glyph, 13, glyphBrush);
        tick.Width = 18;

        var row = Ui.Columns(1,
            tick,
            Ui.Mono(name, 13, Palette.TextBrush),
            Ui.Mono(detail, 12, Palette.TextMutedBrush));
        row.Margin = new Thickness(0, 0, 0, 6);
        return row;
    }

    /// <summary>Write one Markdown per audio file. UTF-8, LF, no BOM — section 10.</summary>
    private void WriteMarkdown()
    {
        try
        {
            foreach (var part in _parts)
            {
                var markdown = MarkdownExporter.Build(_session, part, _parts.Count);
                File.WriteAllText(_session.MarkdownPathOf(part), markdown, new UTF8Encoding(false));
            }

            _session.Completed = true;
            SessionStore.Save(_session);
            RefreshFiles("written");
        }
        catch (Exception ex)
        {
            RefreshFiles(ex.Message);
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

        if (_session.Transcript.Count > 0)
        {
            var words = TranscriptMapper.WordCount(_session.Transcript);
            var coverage = TranscriptMapper.Coverage(_session.Transcript);
            var line = Ui.Text(
                words.ToString("N0") + " words recognised, covering " + Ui.Clock(coverage) +
                " of the recording" +
                (_session.TranscriptionDescription.Length > 0
                    ? " · " + _session.TranscriptionDescription
                    : ""),
                12.5, Palette.AccentTextBrush);
            line.Margin = new Thickness(0, 10, 0, 0);
            _summary.Children.Add(line);

            if (_session.TranscriptionDroppedSeconds >= 1)
            {
                var behind = Ui.Wrap(
                    "Recognition fell " + Ui.Clock(_session.TranscriptionDroppedSeconds) +
                    " behind and that stretch has no text. The audio itself is complete — " +
                    "it can be transcribed from the MP3.",
                    11.5, Palette.WarnBrush);
                behind.Margin = new Thickness(0, 6, 0, 0);
                _summary.Children.Add(behind);
            }
        }
    }

    private static UIElement SummaryRow(string name, Brush colour, double seconds, double total,
                                        bool muted = false)
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

    /// <summary>Copy every Markdown, so a split session can still be pasted in one go.</summary>
    private void CopyMarkdown()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var part in _parts)
            {
                var path = _session.MarkdownPathOf(part);
                if (!File.Exists(path)) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                if (_parts.Count > 1) sb.Append("<!-- ").Append(Path.GetFileName(path)).Append(" -->\n");
                sb.Append(File.ReadAllText(path));
            }

            Clipboard.SetText(sb.ToString());
            _copyButton.Content = _parts.Count > 1 ? "Copied " + _parts.Count + " files" : "Copied";
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
