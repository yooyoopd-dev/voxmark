using System.Text;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Writes the session Markdown exactly per the design guide's output
/// contract (section 10). An LLM consumes this file, so the shape is fixed:
/// field order, the separate "## Gaps" table, and the
/// <c>HH:MM:SS.mmm</c> timestamps measured from the start of
/// <c>audio_file</c> are all load-bearing. The guide flags the format as its
/// own proposal rather than a settled requirement — so changing it is a real
/// decision to confirm, not a refactor.
///
/// UTF-8, LF, no BOM.
/// </summary>
public static class MarkdownExporter
{
    public const string ToolVersion = "VoxMark 1.1";

    /// <summary>The Markdown for a whole, unsplit session.</summary>
    public static string Build(RecordingSession session)
    {
        var whole = new AudioPart
        {
            Index = 1,
            FileName = session.AudioFileName,
            StartSeconds = 0,
            EndSeconds = session.AudioDurationSeconds,
        };
        return Build(session, whole, partCount: 1);
    }

    /// <summary>
    /// The Markdown for one audio file of a session.
    ///
    /// With <paramref name="partCount"/> at 1 this is exactly the section 10
    /// contract. When a session was split into several MP3s, each file gets
    /// its own Markdown covering only the marks in that file — but the
    /// timestamps keep counting from the start of part 1 rather than
    /// restarting, so a given time means the same thing in every file. The
    /// extra <c>audio_part_start</c> field is what lets a reader seek inside
    /// the file it is holding: subtract it from any timestamp here.
    /// </summary>
    public static string Build(RecordingSession session, AudioPart part, int partCount)
    {
        var sb = new StringBuilder();
        var split = partCount > 1;

        var all = session.Marks.OrderBy(m => m.StartSeconds).ThenBy(m => m.Id).ToList();

        // Session-wide numbering: a mark keeps its number in whichever file
        // it lands in, so a note about "mark 27" resolves across the set.
        var numbers = new Dictionary<long, int>();
        for (var i = 0; i < all.Count; i++) numbers[all[i].Id] = i + 1;

        var marks = split
            ? all.Where(m => part.Covers(m.StartSeconds, m.EndSeconds))
                 .Select(m => Clip(m, part))
                 .ToList()
            : all;

        var gaps = split
            ? session.Gaps.Where(g => part.Covers(g.Start, g.End))
                          .Select(g => new Gap
                          {
                              Start = Math.Max(g.Start, part.StartSeconds),
                              End = Math.Min(g.End, part.EndSeconds),
                          })
                          .Where(g => g.Duration > 0)
                          .ToList()
            : session.Gaps;

        sb.Append("---\n");
        sb.Append("title: ").Append(Yaml(session.Title)).Append('\n');
        sb.Append("date: ").Append(session.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz")).Append('\n');
        sb.Append("duration: ").Append(Timestamp(part.DurationSeconds)).Append('\n');
        sb.Append("audio_file: ").Append(part.FileName).Append('\n');
        sb.Append("audio_format: ").Append(session.AudioFormatDescription).Append('\n');

        if (split)
        {
            sb.Append("audio_part: ").Append(part.Index).Append(" of ").Append(partCount).Append('\n');
            sb.Append("audio_part_start: ").Append(Timestamp(part.StartSeconds)).Append('\n');
            sb.Append("audio_part_end: ").Append(Timestamp(part.EndSeconds)).Append('\n');
            sb.Append("session_duration: ").Append(Timestamp(session.AudioDurationSeconds)).Append('\n');
            sb.Append("timebase: offset from the start of part 1, continuing across every part; ");
            sb.Append("subtract audio_part_start to seek inside audio_file\n");
        }
        else
        {
            sb.Append("timebase: offset from start of audio_file\n");
        }

        sb.Append("paused_total: ").Append(Timestamp(session.PausedTotalSeconds)).Append('\n');
        sb.Append("wall_clock_end: ").Append(session.EndedAt.ToString("yyyy-MM-ddTHH:mm:sszzz")).Append('\n');
        sb.Append("marking: manual, single operator\n");
        sb.Append("speakers:\n");

        // Absent speakers keep their slot but are left out of the roster, so
        // an id can legitimately be missing from this list (section 10).
        foreach (var speaker in session.PresentSpeakers.OrderBy(s => s.SlotIndex))
        {
            sb.Append("  - id: ").Append(speaker.Id).Append('\n');
            sb.Append("    name: ").Append(Yaml(speaker.Name)).Append('\n');
            sb.Append("    role: ").Append(Yaml(speaker.Role)).Append('\n');
        }

        sb.Append("unmarked_duration: ").Append(Timestamp(gaps.Sum(g => g.Duration))).Append('\n');
        sb.Append("tool: ").Append(ToolVersion).Append('\n');
        sb.Append("---\n\n");

        sb.Append("# ").Append(session.Title);
        if (split) sb.Append(" — part ").Append(part.Index).Append(" of ").Append(partCount);
        sb.Append("\n\n");

        sb.Append("Speaker segments in chronological order. Each row is one continuous\n");
        sb.Append("turn marked by the operator during the meeting.\n");
        if (split)
        {
            sb.Append("\nTimes are offsets from the start of part 1, not from the start of\n");
            sb.Append("this file. This file begins at ").Append(Timestamp(part.StartSeconds))
              .Append(" — subtract that to seek within it.\n");
        }
        sb.Append('\n');
        sb.Append("| # | speaker | name | start | end | duration |\n");
        sb.Append("|---|---------|------|-------|-----|----------|\n");

        foreach (var mark in marks)
        {
            var speaker = session.SpeakerForSlot(mark.SpeakerSlot);
            sb.Append("| ").Append(numbers.TryGetValue(mark.Id, out var number) ? number : 0)
              .Append(" | ").Append(speaker?.Id ?? "S?")
              .Append(" | ").Append(Cell(speaker?.Name ?? "Unknown"))
              .Append(" | ").Append(Timestamp(mark.StartSeconds))
              .Append(" | ").Append(Timestamp(mark.EndSeconds))
              .Append(" | ").Append(Timestamp(mark.DurationSeconds))
              .Append(" |\n");
        }

        sb.Append("\n## Gaps\n\n");
        sb.Append("Ranges with no speaker marked. Transcribe these, but attribute with care.\n\n");
        sb.Append("| start | end | duration |\n");
        sb.Append("|-------|-----|----------|\n");
        foreach (var gap in gaps)
        {
            sb.Append("| ").Append(Timestamp(gap.Start))
              .Append(" | ").Append(Timestamp(gap.End))
              .Append(" | ").Append(Timestamp(gap.Duration))
              .Append(" |\n");
        }

        sb.Append("\n## Notes\n\n");
        if (split)
        {
            sb.Append("- This session was recorded as ").Append(partCount)
              .Append(" MP3 files; this is part ").Append(part.Index)
              .Append(", covering ").Append(Timestamp(part.StartSeconds))
              .Append(" to ").Append(Timestamp(part.EndSeconds))
              .Append(" of the session.\n");
            sb.Append("- All times are offsets from the start of part 1. To seek inside this\n");
            sb.Append("  file, subtract audio_part_start (")
              .Append(Timestamp(part.StartSeconds)).Append(").\n");
            sb.Append("- A turn that ran across the file boundary appears in both files, cut at\n");
            sb.Append("  the boundary, so neither file loses the speech.\n");
        }
        else
        {
            sb.Append("- All times are offsets into audio_file.\n");
        }

        if (session.PauseCount > 0)
        {
            sb.Append("- Recording was paused ").Append(session.PauseCount)
              .Append(session.PauseCount == 1 ? " time" : " times")
              .Append("; the paused time is absent from both the audio and this table.\n");
        }
        sb.Append("- Mark starts are shifted ")
          .Append(session.Options.MarkStartOffsetSeconds.ToString("0.#"))
          .Append(" s earlier than the operator's key press.\n");

        foreach (var mark in marks)
        {
            if (!mark.AutoClosed) continue;
            var speaker = session.SpeakerForSlot(mark.SpeakerSlot);
            sb.Append("- Mark ").Append(numbers.TryGetValue(mark.Id, out var number) ? number : 0)
              .Append(" (").Append(speaker?.Id ?? "S?").Append(", ")
              .Append(Timestamp(mark.StartSeconds))
              .Append(") was auto-closed when recording stopped.\n");
        }

        sb.Append(session.Options.AllowOverlappingMarks
            ? "- Overlapping marks were allowed; two rows may cover the same range.\n"
            : "- Overlapping speech was not recorded separately in this session.\n");

        if (session.DroppedBufferCount > 0)
        {
            sb.Append("- ").Append(session.DroppedBufferCount)
              .Append(" capture buffer(s) were dropped during encoding; the audio may skip briefly.\n");
        }

        foreach (var note in session.Notes)
        {
            sb.Append("- ").Append(note).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// A mark cut down to the span that actually falls inside one part. The
    /// clone keeps the original id so a turn spanning a boundary carries the
    /// same number in both files.
    /// </summary>
    private static Mark Clip(Mark mark, AudioPart part)
    {
        if (mark.StartSeconds >= part.StartSeconds && mark.EndSeconds <= part.EndSeconds) return mark;

        var clipped = mark.Clone();
        clipped.StartSeconds = Math.Max(mark.StartSeconds, part.StartSeconds);
        clipped.EndSeconds = Math.Min(mark.EndSeconds, part.EndSeconds);
        return clipped;
    }

    /// <summary>HH:MM:SS.mmm, offset from the start of the audio file.</summary>
    public static string Timestamp(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds)) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ((int)ts.TotalHours).ToString("00") + ":" + ts.Minutes.ToString("00") + ":" +
               ts.Seconds.ToString("00") + "." + ts.Milliseconds.ToString("000");
    }

    /// <summary>Quote a YAML scalar only when it would otherwise be misread.</summary>
    private static string Yaml(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var needsQuotes = value.IndexOfAny(new[] { ':', '#', '"', '\'', '\n', '\r', '\t' }) >= 0
                          || value != value.Trim()
                          || "-?[]{}&*!|>%@`,".Contains(value[0]);
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                           .Replace("\r", "").Replace("\n", " ") + "\"";
    }

    /// <summary>A pipe inside a name would split the table row.</summary>
    private static string Cell(string value) => value.Replace("|", "\\|");
}
