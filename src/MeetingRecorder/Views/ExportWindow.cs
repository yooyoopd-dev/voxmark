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
/// the Markdown that goes with it — and one further Markdown covering the
/// whole meeting, because the split belongs to the audio, not to the meeting.
/// </summary>
public sealed class ExportWindow : ShellWindow
{
    private readonly RecordingSession _session;
    private readonly List<AudioPart> _parts;
    private readonly StackPanel _files = new();
    private readonly StackPanel _summary = new();
    private readonly StackPanel _corrections = new();
    private Border? _correctionsCard;
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
        BuildCorrections();
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
                  " files; each one gets its own Markdown, plus one more covering the whole meeting. " +
                  "The timestamps keep counting from the first file."
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

        // The words the operator corrected by hand, offered as dictionary
        // rules. Hidden until there is something to offer.
        _correctionsCard = Ui.Card(Ui.Vertical(10,
            Ui.Section("Corrections you made"),
            Ui.Wrap("Words you changed in the transcript, most-repeated first. Add one to the dictionary and " +
                    "from the next meeting the recogniser's version is rewritten to yours automatically, and " +
                    "your spelling joins the vocabulary it is prompted with. Review and edit the rules in " +
                    "Settings ▸ Speech recognition ▸ Dictionary.", 12, Palette.TextMutedBrush),
            _corrections),
            new Thickness(15));
        _correctionsCard.Margin = new Thickness(0, 12, 0, 0);
        _correctionsCard.Visibility = Visibility.Collapsed;

        var openFolder = Ui.MakeButton("Open folder", null, "GhostButton", (_, _) => OpenFolder());
        openFolder.MinHeight = 40;

        // Only offered when there are words to review: a button that opens an
        // empty list is a question the operator cannot answer.
        var review = Ui.MakeButton("Review transcript…", null, "GhostButton", (_, _) => ReviewTranscript());
        review.MinHeight = 40;
        review.Margin = new Thickness(10, 0, 0, 0);
        review.Visibility = _session.Transcript.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var done = Ui.MakeButton("Done", null, "AccentButton", (_, _) => Finish());

        var actions = Ui.Columns(3, openFolder, _copyButton, review, Ui.Filler(), done);
        actions.Margin = new Thickness(0, 20, 0, 0);

        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(blurb);
        content.Children.Add(filesCard);
        content.Children.Add(summaryCard);
        content.Children.Add(_correctionsCard);
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
        _filesHeading.Text = ("Files · " + (_parts.Count * 2 + (_parts.Count > 1 ? 1 : 0))).ToUpperInvariant();

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
                : markdownState == "pending" ? "Writing…" : markdownState;

            _files.Children.Add(FileRow(glyph, brush, Path.GetFileName(markdownPath), detail));
        }

        if (_parts.Count > 1)
        {
            var combinedPath = _session.CombinedMarkdownPath;
            var combinedWritten = File.Exists(combinedPath);
            _files.Children.Add(FileRow(
                combinedWritten ? "✓" : markdownState == "pending" ? "◐" : "✕",
                combinedWritten ? Palette.GoodBrush
                    : markdownState == "pending" ? Palette.AccentBrush : Palette.RecBrush,
                Path.GetFileName(combinedPath),
                combinedWritten ? FileSize(combinedPath) + " · whole session"
                    : markdownState == "pending" ? "Writing…" : markdownState));

            _files.Children.Add(Ui.Wrap(
                "Every per-part Markdown carries audio_part_start — subtract it from a timestamp to seek inside " +
                "that file. " + Path.GetFileName(combinedPath) + " holds the whole meeting in one document; " +
                "the MP3s are not joined.",
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

            // Plus one covering the whole meeting. The audio stays split —
            // joining MP3s would mean re-encoding — but the record of who
            // spoke when is one meeting's worth of information and reads far
            // better as one file.
            if (_parts.Count > 1)
            {
                File.WriteAllText(_session.CombinedMarkdownPath,
                    MarkdownExporter.BuildCombined(_session, _parts), new UTF8Encoding(false));
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
            if (!File.Exists(path)) return "Not written";
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

    /// <summary>Copy the whole session's Markdown, in one paste.</summary>
    private void CopyMarkdown()
    {
        try
        {
            // One document beats a concatenation of several, so a split
            // session copies the combined file rather than every part.
            if (_parts.Count > 1 && File.Exists(_session.CombinedMarkdownPath))
            {
                Clipboard.SetText(File.ReadAllText(_session.CombinedMarkdownPath));
                _copyButton.Content = "Copied the whole session";
                return;
            }

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

    /// <summary>
    /// Open the whole transcript for correction, then rewrite every Markdown
    /// if anything changed. The export is a view of the session, so the
    /// corrected words reach the file by the same path the originals did —
    /// there is no second exporter to keep in step.
    /// </summary>
    private void ReviewTranscript()
    {
        try
        {
            var editor = new TranscriptEditorWindow(_session) { Owner = this };
            editor.ShowDialog();
            if (!editor.Changed) return;

            _session.Transcript = _session.Transcript.OrderBy(t => t.StartSeconds).ToList();
            BuildSummary();
            BuildCorrections();
            RefreshFiles("pending");
            _copyButton.Content = "Copy Markdown";
            Dispatcher.InvokeAsync(WriteMarkdown, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            AppPaths.Note("The transcript review could not be opened: " + ex.Message);
        }
    }

    /// <summary>The most suggestions shown; past this the list is a chore, not an offer.</summary>
    private const int MaxSuggestions = 30;

    /// <summary>
    /// List the word swaps the operator made by hand — on the live strip or
    /// in the review window — read back from the transcript journal, each
    /// with a button that turns it into a dictionary rule. One at a time on
    /// purpose: a swap that was right for one sentence ("a" for "the") is not
    /// automatically a rule, and only the operator can tell which is which.
    /// </summary>
    private void BuildCorrections()
    {
        if (_correctionsCard is null) return;
        _corrections.Children.Clear();

        List<TranscriptDictionary.Suggestion> suggestions;
        TranscriptionSettingsStore.Settings settings;
        try
        {
            suggestions = TranscriptDictionary.Suggest(TranscriptStore.ReadEdits(_session.TranscriptPath));
            settings = TranscriptionSettingsStore.Load();
        }
        catch (Exception ex)
        {
            AppPaths.Note("The transcript corrections could not be read: " + ex.Message);
            suggestions = new List<TranscriptDictionary.Suggestion>();
            settings = new TranscriptionSettingsStore.Settings();
        }

        _correctionsCard.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var suggestion in suggestions.Take(MaxSuggestions))
        {
            _corrections.Children.Add(CorrectionRow(suggestion, settings));
        }

        if (suggestions.Count > MaxSuggestions)
        {
            _corrections.Children.Add(Ui.Text(
                "+ " + (suggestions.Count - MaxSuggestions) + " more, each made once", 12, Palette.TextMutedBrush));
        }
    }

    private UIElement CorrectionRow(TranscriptDictionary.Suggestion suggestion, TranscriptionSettingsStore.Settings settings)
    {
        var rule = new TranscriptDictionary.Correction(suggestion.Heard, suggestion.Correct);
        var list = TranscriptDictionary.ListForSuggestion(
            _session.Options.TranscriptionLanguage, suggestion.Heard, suggestion.Correct);
        var language = TranscriptDictionary.LanguageName(list);

        var label = Ui.Mono(suggestion.Heard + "  →  " + suggestion.Correct, 13, Palette.TextBrush);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;

        var count = Ui.Mono(suggestion.Count > 1 ? "×" + suggestion.Count : "", 12, Palette.TextMutedBrush);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(10, 0, 10, 0);

        var add = Ui.MakeButton("Add to " + language + " dictionary", null, "ChipButton");
        add.MinHeight = 26;

        void MarkAdded()
        {
            add.Content = "✓ In the " + language + " dictionary";
            add.IsEnabled = false;
        }

        if (TranscriptDictionary.Contains(TranscriptDictionary.CorrectionsFor(settings, list), rule))
        {
            MarkAdded();
        }

        add.Click += (_, _) =>
        {
            try
            {
                // Read-modify-write, like every other writer of this file.
                var current = TranscriptionSettingsStore.Load();
                var existing = TranscriptDictionary.CorrectionsFor(current, list);
                if (!TranscriptDictionary.Contains(existing, rule))
                {
                    TranscriptDictionary.Set(current, list,
                        TranscriptDictionary.VocabularyFor(current, list),
                        TranscriptDictionary.Append(existing, rule));
                    TranscriptionSettingsStore.Save(current);
                }
                MarkAdded();
            }
            catch (Exception ex)
            {
                AppPaths.Note("The correction could not be added to the dictionary: " + ex.Message);
            }
        };

        var row = Ui.Columns(0, label, count, add);
        row.Margin = new Thickness(0, 0, 0, 6);
        return row;
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
