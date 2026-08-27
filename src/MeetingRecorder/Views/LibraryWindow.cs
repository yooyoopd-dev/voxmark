using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeetingRecorder.Controls;
using MeetingRecorder.Models;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// S1 · Library — design guide section 07. A row is one folder. Deleting a
/// session in the app only unlists it; the guide instructs the agent never
/// to delete the user's audio, so nothing here removes a file.
///
/// This is also the crash-recovery entry point: a session whose
/// <c>session.json</c> was never marked complete offers "Finish export",
/// which rebuilds the Markdown from the <c>marks.jsonl</c> journal.
/// </summary>
public sealed class LibraryWindow : ShellWindow
{
    private readonly StackPanel _rows = new();

    public LibraryWindow() : base("VoxMark", 1000, 720)
    {
        MinWidth = 760;
        MinHeight = 520;

        var heading = Ui.Vertical(2,
            Ui.Text("Sessions", 24),
            Ui.Text("stored locally · nothing leaves this machine", 12.5, Palette.TextMutedBrush));

        var newMeeting = Ui.MakeButton("＋ New meeting", null, "AccentButton", (_, _) => StartNewMeeting());
        newMeeting.VerticalAlignment = VerticalAlignment.Bottom;

        var header = Ui.Columns(1, heading, Ui.Filler(), newMeeting);
        header.Margin = new Thickness(0, 0, 0, 18);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rows,
        };

        var footnote = Ui.Wrap(
            "A row is one folder. Removing a session here only unlists it — VoxMark never deletes your audio.",
            12.5, Palette.TextMutedBrush);
        footnote.Margin = new Thickness(0, 16, 0, 0);

        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 20), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footnote, Dock.Bottom);
        body.Children.Add(header);
        body.Children.Add(footnote);
        body.Children.Add(scroller);

        SetBody(body);

        Loaded += (_, _) => Reload();
        Activated += (_, _) => Reload();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            StartNewMeeting();
            e.Handled = true;
        }
    }

    private void StartNewMeeting()
    {
        var setup = new SetupWindow();
        setup.Show();
        Close();
    }

    private void Reload()
    {
        _rows.Children.Clear();

        List<RecordingSession> sessions;
        try
        {
            sessions = SessionStore.ListSessions();
        }
        catch (Exception)
        {
            sessions = new List<RecordingSession>();
        }

        if (sessions.Count == 0)
        {
            _rows.Children.Add(EmptyState());
            return;
        }

        foreach (var session in sessions)
        {
            _rows.Children.Add(Row(session));
        }
    }

    private UIElement EmptyState()
    {
        var content = Ui.Vertical(8,
            Ui.Text("No meetings recorded yet", 16, Palette.TextBodyBrush),
            Ui.Wrap("Start one and VoxMark writes an MP3 plus a Markdown file of speaker segments into " +
                    AppPaths.SessionsRoot + ".", 13, Palette.TextMutedBrush));
        var card = Ui.Card(content, new Thickness(20), Palette.HairlineSoftBrush, Palette.WellBrush);
        return card;
    }

    private UIElement Row(RecordingSession session)
    {
        var recovered = !session.Completed;

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Ui.Text(session.Title, 15));
        if (recovered)
        {
            title.Children.Add(Ui.Text(" · recovered", 12, Palette.RecBrush));
        }

        var meta = recovered && session.AudioDurationSeconds <= 0
            ? session.StartedAt.ToString("yyyy-MM-dd HH:mm") + " · app closed unexpectedly"
            : session.StartedAt.ToString("yyyy-MM-dd HH:mm") +
              " · " + Ui.Clock(session.AudioDurationSeconds) +
              " · " + session.Speakers.Count + " speakers" +
              " · " + session.Marks.Count + " marks";

        var identity = Ui.Vertical(2, title, Ui.Mono(meta, 11.5, Palette.TextMutedBrush));

        var strip = new BlockLaneView
        {
            TotalSeconds = Math.Max(1, session.AudioDurationSeconds),
            BlockInset = 4,
            IsInteractive = false,
        };
        strip.SetMarks(session.Marks);

        var stripWell = Ui.Well(strip, new Thickness(0), 4);
        stripWell.Width = 150;
        stripWell.Height = 22;
        stripWell.VerticalAlignment = VerticalAlignment.Center;
        stripWell.Margin = new Thickness(14, 0, 14, 0);

        var hasMarkdown = File.Exists(session.MarkdownPath);
        var hasAudio = File.Exists(session.Mp3Path);
        var filesLabel = Ui.Mono(
            hasAudio && hasMarkdown ? "mp3 + md" : hasAudio ? "mp3 only" : "no files",
            11.5,
            hasAudio && hasMarkdown ? Palette.GoodBrush : Palette.WarnBrush);
        filesLabel.Margin = new Thickness(0, 0, 14, 0);

        var action = recovered
            ? Ui.MakeButton("Finish export", null, "ChipButton", (_, _) => FinishExport(session))
            : Ui.MakeButton("Open folder", null, "LinkButton", (_, _) => OpenFolder(session.SessionFolder));
        if (recovered) action.Foreground = Palette.RecBrush;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(identity, 0);
        Grid.SetColumn(stripWell, 1);
        Grid.SetColumn(filesLabel, 2);
        Grid.SetColumn(action, 3);
        row.Children.Add(identity);
        row.Children.Add(stripWell);
        row.Children.Add(filesLabel);
        row.Children.Add(action);

        if (recovered) stripWell.Visibility = Visibility.Collapsed;

        var card = Ui.Card(row, new Thickness(15, 13, 15, 13),
            recovered ? Palette.RecEdgeBrush : Palette.HairlineBrush);
        card.Margin = new Thickness(0, 0, 0, 7);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!recovered) OpenFolder(session.SessionFolder);
        };
        return card;
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a folder is a convenience; never take the app down for it.
        }
    }

    /// <summary>
    /// Rebuild an interrupted session from its journal and write the Markdown
    /// it never got to write. Section 08: "if the machine dies, S1 offers a
    /// one-click recovery that rebuilds the Markdown from the journal."
    /// </summary>
    private void FinishExport(RecordingSession session)
    {
        try
        {
            var (marks, open) = SessionStore.Recover(session);

            var duration = session.AudioDurationSeconds > 0
                ? session.AudioDurationSeconds
                : SessionStore.EstimateMp3Seconds(session.Mp3Path, session.Options.Mp3BitrateKbps);

            foreach (var pending in open)
            {
                // Anything still open when the machine died is closed at the
                // end of the audio and flagged, exactly like a Stop would.
                marks.Add(new Mark
                {
                    Id = (marks.Count == 0 ? 0 : marks.Max(m => m.Id)) + 1,
                    SpeakerSlot = pending.SpeakerSlot,
                    StartSeconds = pending.StartSeconds,
                    EndSeconds = Math.Max(pending.StartSeconds + 0.05, duration),
                    RawPressSeconds = pending.RawPressSeconds,
                    AutoClosed = true,
                });
            }

            foreach (var mark in marks)
            {
                if (duration > 0 && mark.EndSeconds > duration) mark.EndSeconds = duration;
            }
            marks.RemoveAll(m => m.DurationSeconds <= 0);

            session.Marks = marks.OrderBy(m => m.StartSeconds).ToList();
            session.AudioDurationSeconds = duration;
            if (session.EndedAt == default) session.EndedAt = session.StartedAt.AddSeconds(duration);

            var engine = new MarkingEngine(session.Options);
            engine.LoadRecovered(session.Marks, Array.Empty<OpenMark>());
            session.Gaps = engine.ComputeGaps(duration).ToList();
            session.Notes.Add("This session was recovered from the on-disk journal after the app closed unexpectedly.");
            session.Completed = true;

            File.WriteAllText(session.MarkdownPath, MarkdownExporter.Build(session), new System.Text.UTF8Encoding(false));
            SessionStore.Save(session);

            var export = new ExportWindow(session, alreadyWritten: true);
            export.Show();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not finish this session: " + ex.Message, "VoxMark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
