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
    private readonly StackPanel _plans = new();
    private readonly TextBlock _plansHeading;
    private readonly Border _plansSection;
    private readonly TextBlock _notice;

    public LibraryWindow() : base("VoxMark", 1000, 720)
    {
        MinWidth = 760;
        MinHeight = 520;

        var heading = Ui.Vertical(2,
            Ui.Text("Sessions", 24),
            Ui.Text(BuildProfile.Subtitle, 12.5, Palette.TextMutedBrush));

        var settings = Ui.MakeButton("Settings", null, "GhostButton", (_, _) => OpenSettings());
        settings.VerticalAlignment = VerticalAlignment.Bottom;
        settings.Margin = new Thickness(0, 0, 10, 0);

        var newMeeting = Ui.MakeButton("＋ New meeting", null, "AccentButton", (_, _) => StartNewMeeting());
        newMeeting.VerticalAlignment = VerticalAlignment.Bottom;

        var headerRow = Ui.Columns(1, heading, Ui.Filler(), settings, newMeeting);

        // Anything that fails here fails on a click, with no status line to
        // fall back on — before this, a store that could not write simply
        // took the app down (see AppPaths.EnsureRoot).
        _notice = Ui.Wrap("", 12.5, Palette.RecBrush);
        _notice.Visibility = Visibility.Collapsed;
        _notice.Margin = new Thickness(0, 10, 0, 0);

        var header = Ui.Vertical(0, headerRow, _notice);
        header.Margin = new Thickness(0, 0, 0, 18);

        _plansHeading = Ui.Section("Ready to record · 0");
        _plansSection = new Border
        {
            Margin = new Thickness(0, 0, 0, 20),
            Child = Ui.Vertical(10,
                Ui.Columns(0,
                    _plansHeading,
                    Ui.Text("Meetings you set up earlier — pick one to open its setup", 12,
                        Palette.TextMutedBrush)),
                _plans),
        };

        var stack = new StackPanel();
        stack.Children.Add(_plansSection);
        stack.Children.Add(Ui.Section("Recorded sessions"));
        _rows.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_rows);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
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

    /// <summary>
    /// The machine-wide settings — save location, recording defaults, the
    /// speech model, the diagnostics log. They belong to the PC rather than
    /// to one meeting, so they live a click away from the library instead of
    /// on the setup screen the operator walks through before every meeting.
    /// </summary>
    private void OpenSettings()
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        Reload();
    }

    private void ShowNotice(string message)
    {
        _notice.Text = message;
        _notice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Reload()
    {
        ReloadPlans();
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

    /// <summary>
    /// The meetings the operator prepared in advance. Section 07 gives the
    /// library "past sessions, new meeting"; this is the third thing an
    /// operator walking into a room actually wants — the meeting they already
    /// set up, one click from Start.
    /// </summary>
    private void ReloadPlans()
    {
        _plans.Children.Clear();

        List<MeetingPlan> plans;
        try
        {
            plans = PlanStore.Load();
        }
        catch (Exception)
        {
            plans = new List<MeetingPlan>();
        }

        _plansHeading.Text = ("Ready to record · " + plans.Count).ToUpperInvariant();
        _plansSection.Visibility = plans.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var plan in plans)
        {
            _plans.Children.Add(PlanRow(plan));
        }
    }

    private UIElement PlanRow(MeetingPlan plan)
    {
        var roster = plan.Speakers.Count == 0
            ? "No roster yet"
            : plan.Speakers.Count + " speakers · " +
              string.Join(", ", plan.Speakers.Take(3).Select(s => s.Name)) +
              (plan.Speakers.Count > 3 ? " …" : "");

        var identity = Ui.Vertical(2,
            Ui.Text(plan.Title, 15),
            Ui.Mono(plan.WhenLabel + (string.IsNullOrWhiteSpace(plan.Room) ? "" : " · " + plan.Room) +
                    " · " + roster, 11.5, Palette.TextMutedBrush));

        var options = Ui.Mono(
            plan.Options.SplitMinutes > 0 ? "split " + plan.Options.SplitMinutes + " min" : "one file",
            11.5, Palette.TextMutedBrush);
        options.Margin = new Thickness(0, 0, 14, 0);

        var open = Ui.MakeButton("Open setup", null, "ChipButtonAccent", (_, _) => OpenPlan(plan));
        var remove = Ui.MakeButton("✕", null, "ChipButton", (_, _) =>
        {
            try
            {
                PlanStore.Remove(plan.Id);
                ShowNotice("");
            }
            catch (Exception ex)
            {
                AppPaths.Note("Could not update \"" + AppPaths.Root + "\\plans.json\".\n" +
                              ex.GetType().Name + ": " + ex.Message + "\n" +
                              AppPaths.OneDriveHint(AppPaths.Root));
                ShowNotice("Could not forget that setup — " + ex.Message +
                           " See Settings → Log for the full diagnostic.");
            }

            ReloadPlans();
        });
        remove.Foreground = Palette.TextMutedBrush;
        remove.Margin = new Thickness(8, 0, 0, 0);
        remove.ToolTip = "Forget this saved setup";

        var row = Ui.Columns(0, identity, options, open, remove);
        var card = Ui.Card(row, new Thickness(15, 13, 15, 13), Palette.AccentEdgeBrush);
        card.Margin = new Thickness(0, 0, 0, 7);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, _) => OpenPlan(plan);
        return card;
    }

    private void OpenPlan(MeetingPlan plan)
    {
        new SetupWindow(plan).Show();
        Close();
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
                : session.AudioParts.Sum(p => SessionStore.EstimateMp3Seconds(
                      session.PathOf(p), session.Options.Mp3BitrateKbps));
            if (duration <= 0)
            {
                duration = SessionStore.EstimateMp3Seconds(session.Mp3Path, session.Options.Mp3BitrateKbps);
            }

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

            // A recovered session may have rolled through several MP3s, so it
            // gets the same one-Markdown-per-file treatment as a clean stop.
            var parts = session.EffectiveParts();
            if (parts.Count > 0) parts[^1].EndSeconds = Math.Max(parts[^1].EndSeconds, duration);
            foreach (var part in parts)
            {
                File.WriteAllText(
                    session.MarkdownPathOf(part),
                    MarkdownExporter.Build(session, part, parts.Count),
                    new System.Text.UTF8Encoding(false));
            }

            // And the whole-session document, exactly as a clean stop writes it.
            if (parts.Count > 1)
            {
                File.WriteAllText(
                    session.CombinedMarkdownPath,
                    MarkdownExporter.BuildCombined(session, parts),
                    new System.Text.UTF8Encoding(false));
            }
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
