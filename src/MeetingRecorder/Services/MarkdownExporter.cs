using System.Text;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Writes the session Markdown exactly per the design guide's "Output
/// contract" (section 10) — an LLM consumes this file, so the shape is
/// fixed, not a suggestion. Don't reformat this without re-reading that
/// section: field order, the Gaps section, and the timestamp format
/// (HH:MM:SS.mmm, offset from the start of audio_file) are all load-bearing.
/// </summary>
public static class MarkdownExporter
{
    public static string Build(RecordingSession session)
    {
        var sb = new StringBuilder();

        sb.Append("---\n");
        sb.Append($"title: {session.Title}\n");
        sb.Append($"date: {session.StartedAt:yyyy-MM-ddTHH:mm:sszzz}\n");
        sb.Append($"duration: {FormatTimestamp(session.AudioDurationSeconds)}\n");
        sb.Append($"audio_file: {session.AudioFileName}\n");
        sb.Append("audio_format: mp3 / 128 kbps / 44100 Hz / mono\n");
        sb.Append("timebase: offset from start of audio_file\n");
        sb.Append($"paused_total: {FormatTimestamp(session.PausedTotalSeconds)}\n");
        sb.Append($"wall_clock_end: {session.EndedAt:yyyy-MM-ddTHH:mm:sszzz}\n");
        sb.Append("marking: manual, single operator\n");
        sb.Append("speakers:\n");
        foreach (var speaker in session.Speakers)
        {
            sb.Append($"  - id: {speaker.Id}\n");
            sb.Append($"    name: {speaker.Name}\n");
            sb.Append($"    role: {speaker.Role}\n");
        }

        var unmarked = session.Gaps.Sum(g => g.End - g.Start);
        sb.Append($"unmarked_duration: {FormatTimestamp(unmarked)}\n");
        sb.Append("tool: MeetingRecorder 0.1\n");
        sb.Append("---\n\n");

        sb.Append($"# {session.Title}\n\n");
        sb.Append("Speaker segments in chronological order. Each row is one continuous\n");
        sb.Append("turn marked by the operator during the meeting.\n\n");
        sb.Append("| # | speaker | name | start | end | duration |\n");
        sb.Append("|---|---------|------|-------|-----|----------|\n");

        var ordered = session.Marks.OrderBy(m => m.StartSeconds).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var mark = ordered[i];
            var speaker = session.Speakers.First(s => s.SlotIndex == mark.SpeakerSlot);
            sb.Append($"| {i + 1} | {speaker.Id} | {speaker.Name} | " +
                      $"{FormatTimestamp(mark.StartSeconds)} | {FormatTimestamp(mark.EndSeconds)} | " +
                      $"{FormatTimestamp(mark.DurationSeconds)} |\n");
        }

        sb.Append("\n## Gaps\n\n");
        sb.Append("Ranges with no speaker marked. Transcribe these, but attribute with care.\n\n");
        sb.Append("| start | end | duration |\n");
        sb.Append("|-------|-----|----------|\n");
        foreach (var gap in session.Gaps)
        {
            sb.Append($"| {FormatTimestamp(gap.Start)} | {FormatTimestamp(gap.End)} | " +
                      $"{FormatTimestamp(gap.End - gap.Start)} |\n");
        }

        sb.Append("\n## Notes\n\n");
        sb.Append("- All times are offsets into audio_file.\n");
        if (session.PausedTotalSeconds > 0)
        {
            sb.Append($"- Recording was paused {session.PauseCount} time(s); the paused time is absent " +
                      "from both the audio and this table.\n");
        }
        sb.Append("- Mark starts are shifted 0.8 s earlier than the operator's key press.\n");
        for (var i = 0; i < ordered.Count; i++)
        {
            var mark = ordered[i];
            if (!mark.AutoClosed) continue;
            var speaker = session.Speakers.First(s => s.SlotIndex == mark.SpeakerSlot);
            sb.Append($"- Mark {i + 1} ({speaker.Id}, {FormatTimestamp(mark.StartSeconds)}) was " +
                      "auto-closed when recording stopped.\n");
        }
        sb.Append("- Overlapping speech was not recorded separately in this session.\n");

        return sb.ToString();
    }

    private static string FormatTimestamp(double totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }
}
