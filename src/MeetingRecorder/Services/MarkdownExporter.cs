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
    public const string ToolVersion = "VoxMark 1.0";

    public static string Build(RecordingSession session)
    {
        var sb = new StringBuilder();
        var marks = session.Marks.OrderBy(m => m.StartSeconds).ThenBy(m => m.Id).ToList();

        sb.Append("---\n");
        sb.Append("title: ").Append(Yaml(session.Title)).Append('\n');
        sb.Append("date: ").Append(session.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz")).Append('\n');
        sb.Append("duration: ").Append(Timestamp(session.AudioDurationSeconds)).Append('\n');
        sb.Append("audio_file: ").Append(session.AudioFileName).Append('\n');
        sb.Append("audio_format: ").Append(session.AudioFormatDescription).Append('\n');
        sb.Append("timebase: offset from start of audio_file\n");
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

        sb.Append("unmarked_duration: ").Append(Timestamp(session.Gaps.Sum(g => g.Duration))).Append('\n');
        sb.Append("tool: ").Append(ToolVersion).Append('\n');
        sb.Append("---\n\n");

        sb.Append("# ").Append(session.Title).Append("\n\n");
        sb.Append("Speaker segments in chronological order. Each row is one continuous\n");
        sb.Append("turn marked by the operator during the meeting.\n\n");
        sb.Append("| # | speaker | name | start | end | duration |\n");
        sb.Append("|---|---------|------|-------|-----|----------|\n");

        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            var speaker = session.SpeakerForSlot(mark.SpeakerSlot);
            sb.Append("| ").Append(i + 1)
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
        foreach (var gap in session.Gaps)
        {
            sb.Append("| ").Append(Timestamp(gap.Start))
              .Append(" | ").Append(Timestamp(gap.End))
              .Append(" | ").Append(Timestamp(gap.Duration))
              .Append(" |\n");
        }

        sb.Append("\n## Notes\n\n");
        sb.Append("- All times are offsets into audio_file.\n");
        if (session.PauseCount > 0)
        {
            sb.Append("- Recording was paused ").Append(session.PauseCount)
              .Append(session.PauseCount == 1 ? " time" : " times")
              .Append("; the paused time is absent from both the audio and this table.\n");
        }
        sb.Append("- Mark starts are shifted ")
          .Append(session.Options.MarkStartOffsetSeconds.ToString("0.#"))
          .Append(" s earlier than the operator's key press.\n");

        for (var i = 0; i < marks.Count; i++)
        {
            if (!marks[i].AutoClosed) continue;
            var speaker = session.SpeakerForSlot(marks[i].SpeakerSlot);
            sb.Append("- Mark ").Append(i + 1).Append(" (").Append(speaker?.Id ?? "S?").Append(", ")
              .Append(Timestamp(marks[i].StartSeconds))
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
