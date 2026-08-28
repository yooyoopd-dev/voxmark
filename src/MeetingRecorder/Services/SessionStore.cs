using System.IO;
using System.Text.Json;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Reads and writes the session folder described in design guide section 10:
/// one folder per meeting holding the MP3, the Markdown, an app-owned
/// <c>session.json</c>, and the append-only <c>marks.jsonl</c> journal.
///
/// The library (S1) is just a listing of these folders, and an unfinished
/// <c>session.json</c> is what makes a crashed meeting recoverable.
/// </summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RecordingSession Create(
        string title, string room, IEnumerable<Speaker> speakers, SessionOptions options,
        string inputDeviceName, DateTimeOffset scheduledAt, DateTimeOffset startedAt)
    {
        var slug = AppPaths.Slugify(title);
        // The folder is named for the meeting's own date, so a session set up
        // in advance files itself where the operator expects to find it.
        var stamp = scheduledAt.ToString("yyyy-MM-dd");
        var folder = UniqueFolder(Path.Combine(AppPaths.SessionsRoot, stamp + "_" + slug));
        Directory.CreateDirectory(folder);

        var baseName = slug + "_" + stamp;
        return new RecordingSession
        {
            Title = title,
            Room = room,
            SessionFolder = folder,
            AudioBaseName = baseName,
            AudioFileName = baseName + ".mp3",
            Speakers = speakers.Select(s => s.Clone()).ToList(),
            Options = options,
            InputDeviceName = inputDeviceName,
            ScheduledAt = scheduledAt,
            StartedAt = startedAt,
        };
    }

    /// <summary>Two meetings with the same title on the same day must not share a folder.</summary>
    private static string UniqueFolder(string preferred)
    {
        if (!Directory.Exists(preferred)) return preferred;
        for (var n = 2; n < 500; n++)
        {
            var candidate = preferred + "-" + n.ToString();
            if (!Directory.Exists(candidate)) return candidate;
        }
        return preferred + "-" + DateTime.Now.ToString("HHmmss");
    }

    /// <summary>Write session.json atomically, so a crash mid-write cannot destroy the old one.</summary>
    public static void Save(RecordingSession session)
    {
        var path = session.SessionJsonPath;
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(session, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static RecordingSession? Load(string folder)
    {
        var path = Path.Combine(folder, "session.json");
        if (!File.Exists(path)) return null;
        try
        {
            var session = JsonSerializer.Deserialize<RecordingSession>(File.ReadAllText(path));
            if (session is null) return null;
            // The folder can be moved by the user; trust where the file
            // actually is over what was recorded when it was written.
            session.SessionFolder = folder;
            return session;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every session on this machine, newest first.</summary>
    public static List<RecordingSession> ListSessions()
    {
        AppPaths.EnsureCreated();
        var sessions = new List<RecordingSession>();
        foreach (var folder in Directory.EnumerateDirectories(AppPaths.SessionsRoot))
        {
            if (Load(folder) is { } session) sessions.Add(session);
        }
        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }

    /// <summary>
    /// Rebuild an interrupted session's marks from its journal. The audio is
    /// whatever the encoder managed to flush, so the recovered marks are
    /// clamped to it and anything still open is auto-closed at the end.
    /// </summary>
    public static (List<Mark> Marks, List<OpenMark> Open) Recover(RecordingSession session)
    {
        var (marks, open) = MarkJournal.Replay(session.JournalPath);
        return (marks, open);
    }

    /// <summary>Best-effort MP3 length, used when finishing a session the app never got to close.</summary>
    public static double EstimateMp3Seconds(string path, int bitrateKbps)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return bytes / (bitrateKbps * 1000.0 / 8.0);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
