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
    public const string ToolVersion = "VoxMark 1.2";

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

        // Clipped into the part's own boundaries the same way marks are
        // (Clip(mark, part), below) — otherwise a segment whose audio
        // straddles a part boundary would appear in full in both files'
        // Markdown, with a displayed time past that file's own end,
        // contradicting the Notes' own claim that a boundary-crossing turn
        // is "cut at the boundary."
        var segments = split
            ? session.Transcript.Where(t => part.Covers(t.StartSeconds, t.EndSeconds))
                     .Select(t => ClipSegment(t, part))
                     .ToList()
            : session.Transcript;

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

        // Appended after the section 10 keys rather than woven among them, so
        // a reader that knows only the original contract sees it unchanged.
        // Absent entirely when nothing was recognised, which is what keeps a
        // Lite-recorded session byte-identical to what it always was.
        if (segments.Count > 0)
        {
            sb.Append("transcription: ").Append(Yaml(session.TranscriptionDescription.Length > 0
                ? "whisper " + session.TranscriptionDescription
                : "whisper")).Append('\n');
            sb.Append("transcript_coverage: ")
              .Append(Timestamp(TranscriptMapper.Coverage(segments))).Append('\n');
        }

        sb.Append("tool: ").Append(ToolVersion).Append('\n');
        sb.Append("---\n\n");

        sb.Append("# ").Append(session.Title);
        if (split) sb.Append(" — part ").Append(part.Index).Append(" of ").Append(partCount);
        sb.Append("\n\n");

        sb.Append("Speaker segments in chronological order. Each row is one continuous\n");
        sb.Append("turn marked by the operator during the meeting.\n");
        sb.Append("\nIf you are an AI agent, read \"## Agent Instructions\" at the end of this\n");
        sb.Append("file first — it is the task this file exists for.\n");
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

        AppendTranscript(sb, session, marks, segments, numbers);

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
        if (segments.Count > 0)
        {
            sb.Append("- Speech was recognised on this machine by whisper");
            if (session.TranscriptionDescription.Length > 0)
            {
                sb.Append(" (").Append(session.TranscriptionDescription).Append(')');
            }
            sb.Append("; the words are the recogniser's, the speaker attribution is the operator's.\n");
            sb.Append("- A transcript segment is attributed to the mark it overlaps most. Segment\n");
            sb.Append("  boundaries follow the recogniser rather than the speaker changes, so one that\n");
            sb.Append("  straddles a handover is given whole to whoever holds more of it.\n");

            if (session.TranscriptionDroppedSeconds >= 1)
            {
                sb.Append("- Recognition fell behind and ")
                  .Append(Timestamp(session.TranscriptionDroppedSeconds))
                  .Append(" of audio was never transcribed; that time is complete in the MP3 " +
                          "and can be re-transcribed from it.\n");
            }
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

        AppendAgentInstructions(sb, part, partCount, segments.Count > 0);

        return sb.ToString();
    }

    /// <summary>
    /// The standing brief for whoever picks this file up — in practice an LLM
    /// handed the Markdown, usually with the MP3. Everything above says what
    /// was recorded; this says what to do with it, so the operator does not
    /// have to write the same prompt after every meeting.
    ///
    /// The brief branches at *read* time rather than here, because the export
    /// cannot know what the agent will actually be given: the MP3 may not
    /// travel with the Markdown. So it names the routes and lets the agent
    /// pick — audio present means transcribe it; audio absent with a
    /// "## Transcript" section present means write the report straight from
    /// that section; neither means say so instead of inventing a meeting.
    ///
    /// It goes last, after the Notes, for the same reason the transcript goes
    /// after the Gaps table: the section 10 contract above it is untouched,
    /// and a reader that only knows that contract loses nothing. The pointer
    /// under the title is what keeps it from being missed down here.
    ///
    /// Written in English regardless of the meeting's own language — it
    /// addresses the agent, not the room — while the report it asks for is
    /// bilingual.
    /// </summary>
    private static void AppendAgentInstructions(StringBuilder sb, AudioPart part, int partCount,
                                                bool hasTranscript)
    {
        var split = partCount > 1;

        sb.Append("\n## Agent Instructions\n\n");
        sb.Append("You are an AI agent. This file records **who held the floor and when**.\n");
        sb.Append("Your job is to pair that with **what was said** and write one new file,\n");
        sb.Append("`transcript.md`, beside this one.\n\n");

        sb.Append("### Step 1 — pick your route\n\n");
        sb.Append("Check what you were actually given, then follow the matching route. Do not\n");
        sb.Append("ask for a file you were not handed.\n\n");

        sb.Append("**Route A — you have the audio");
        if (hasTranscript) sb.Append(" (preferred)");
        sb.Append(".**\n");
        sb.Append(split
            ? "The session's " + partCount + " MP3 files are present.\n"
            : "The MP3 named in `audio_file` is present.\n");
        sb.Append("Transcribe the audio yourself and align it to the speaker rows above, per\n");
        sb.Append("Step 2.\n");
        if (hasTranscript)
        {
            sb.Append("The `## Transcript` section below is machine recognition with no human\n");
            sb.Append("review — use it as a first pass to check and correct against the audio,\n");
            sb.Append("never as finished text.\n");
        }
        sb.Append('\n');

        if (hasTranscript)
        {
            sb.Append("**Route B — you do not have the audio.** This file already carries a\n");
            sb.Append("`## Transcript` section: speech recognised on the recording machine and\n");
            sb.Append("already grouped under the speaker each passage was marked to. That is\n");
            sb.Append("enough. Skip Step 2 entirely, take those blocks as your source text, and\n");
            sb.Append("go straight to Step 3 — produce `transcript.md` and nothing else. Do not\n");
            sb.Append("stop to ask for the MP3.\n\n");
            sb.Append("Two things to carry through on this route: the recognised text was never\n");
            sb.Append("checked against the audio by a person, so keep wording you cannot verify\n");
            sb.Append("rather than \"improving\" it, and say once at the top of `transcript.md`\n");
            sb.Append("that it was built from the embedded recognition rather than the\n");
            sb.Append("recording. If that text is not in English, translate it.\n\n");
        }
        else
        {
            sb.Append("**Route B — you do not have the audio.** This file has no `## Transcript`\n");
            sb.Append("section either, so nothing here records what was said: the speaker table\n");
            sb.Append("above is timings only. Say that plainly and stop. Do not write a\n");
            sb.Append("`transcript.md` from the speaker names and timings alone — a transcript\n");
            sb.Append("of a meeting you cannot hear would be invention, not a transcript.\n\n");
        }

        sb.Append("### Step 2 — align the speech to the speakers (Route A only)\n\n");
        sb.Append("1. Transcribe the audio in **English**. If the meeting was held in another\n");
        sb.Append("   language, translate it into English and say which language it was.\n");
        sb.Append("2. Align what you hear to the speaker rows above **by time**. A row's\n");
        sb.Append("   `start` and `end` bound that speaker's turn, so speech inside that range\n");
        sb.Append("   is theirs. Do not invent speaker changes the table does not show.\n");
        sb.Append("3. Give a passage that straddles two rows to the row it overlaps most.\n");
        sb.Append("   Where the table and your own read of the voices disagree, follow the\n");
        sb.Append("   table — it is a person's live record, and it is the point of this file.\n");
        sb.Append("4. Speech inside a `## Gaps` range has no marked speaker. File it under\n");
        sb.Append("   \"Unmarked\" rather than guessing who was talking.\n\n");

        sb.Append("### Step 3 — write `transcript.md`\n\n");
        // The split rule belongs to both routes, not to Route A's alignment
        // work: a session recorded in parts is still one meeting, and Route B
        // skips Step 2 entirely.
        if (split)
        {
            sb.Append("This session was recorded as ").Append(partCount)
              .Append(" parts and this is part ").Append(part.Index).Append(".\n");
            sb.Append("Whichever route you took, work from all ").Append(partCount)
              .Append(" into a single `transcript.md`\n");
            sb.Append("covering the whole session — not one per part. They already share one\n");
            sb.Append("timebase, so the times line up across files with no adjustment.\n\n");
        }
        sb.Append("Write every timestamp in the same `HH:MM:SS.mmm` form and on the same\n");
        sb.Append("timebase as this file, so the two can be read side by side. Then write\n");
        sb.Append("these four sections, in this order:\n\n");
        sb.Append("1. `## Full Transcript` — the verbatim English transcript in chronological\n");
        sb.Append("   order, one block per speaker turn, each headed with the same mark number,\n");
        sb.Append("   speaker id, name and time range this file uses. This is the record of\n");
        sb.Append("   what was actually said: do not summarise or condense it. Removing pure\n");
        sb.Append("   filler (\"um\", false starts) is the only editing allowed.\n");
        sb.Append("2. `## Executive Summary` — the whole meeting in a few paragraphs of\n");
        sb.Append("   English: what it was about, what was decided, what is still open, and\n");
        sb.Append("   every owner and deadline that was named.\n");
        sb.Append("3. `## Key Points by Speaker` — one English subsection per speaker, giving\n");
        sb.Append("   that person's main points, positions, commitments and open questions.\n");
        sb.Append("4. `## 한국어 리포트` — sections 2 and 3 again in Korean, as\n");
        sb.Append("   `### 전체 회의 요약` and `### 화자별 핵심 내용`. Translate the meaning\n");
        sb.Append("   rather than word for word, and leave names, product names and figures as\n");
        sb.Append("   they were spoken.\n\n");

        sb.Append("Ground every statement in your source. Where it is unclear, write\n");
        sb.Append("`[inaudible HH:MM:SS.mmm]` instead of guessing, and never add a decision,\n");
        sb.Append("number, owner or deadline that nobody said.\n");
    }

    /// <summary>
    /// The recognised words, grouped under the speaker they were attributed
    /// to. Additive to the section 10 contract rather than a change to it:
    /// the segments table and the Gaps table above are untouched, so a reader
    /// that only knows the original format loses nothing, and one that reads
    /// this gets the transcript already split by speaker.
    ///
    /// Nothing is written at all when no speech was recognised, so a session
    /// recorded without transcription produces exactly the file it used to.
    /// </summary>
    private static void AppendTranscript(StringBuilder sb, RecordingSession session,
                                         IReadOnlyList<Mark> marks, IReadOnlyList<TranscriptSegment> segments,
                                         IReadOnlyDictionary<long, int> numbers)
    {
        var blocks = TranscriptMapper.Blocks(segments, marks);
        if (blocks.Count == 0) return;

        sb.Append("\n## Transcript\n\n");
        sb.Append("Recognised speech in chronological order, grouped by the speaker mark it\n");
        sb.Append("falls in. Times are on the same timebase as the table above. Text under\n");
        sb.Append("\"unmarked\" was spoken while no speaker was marked — transcribe it, but\n");
        sb.Append("attribute it with care.\n\n");

        foreach (var block in blocks)
        {
            var text = block.Text;
            if (text.Length == 0) continue;

            sb.Append("### ");
            if (block.Mark is { } mark)
            {
                var speaker = session.SpeakerForSlot(mark.SpeakerSlot);
                sb.Append(numbers.TryGetValue(mark.Id, out var number) ? number : 0)
                  .Append(" · ").Append(speaker?.Id ?? "S?")
                  .Append(" · ").Append(speaker?.Name ?? "Unknown");
            }
            else
            {
                sb.Append("— · unmarked");
            }

            // A marked block's header is the mark's own start/end — the same
            // field the table above prints — so the two sections agree by
            // construction rather than by two independently-derived numbers
            // that can drift apart. Whisper's segment boundaries follow its
            // own decoding, not the speaker changes, so the *words'* actual
            // audio span (block.StartSeconds/EndSeconds) can legitimately
            // extend past the mark either way; that's still true and still
            // the right span for the recognised text, it's just not what
            // belongs in a header meant to say "this is mark N's turn."
            // Unmarked blocks have no mark to match, so they keep the
            // segment-derived span — the only honest source there.
            var headerStart = block.Mark?.StartSeconds ?? block.StartSeconds;
            var headerEnd = block.Mark?.EndSeconds ?? block.EndSeconds;
            sb.Append(" — ").Append(Timestamp(headerStart))
              .Append(" → ").Append(Timestamp(headerEnd)).Append("\n\n");
            sb.Append(text).Append("\n\n");
        }
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

    /// <summary>The transcript-segment equivalent of <see cref="Clip"/>, same shape.</summary>
    private static TranscriptSegment ClipSegment(TranscriptSegment segment, AudioPart part)
    {
        if (segment.StartSeconds >= part.StartSeconds && segment.EndSeconds <= part.EndSeconds) return segment;

        var clipped = segment.Clone();
        clipped.StartSeconds = Math.Max(segment.StartSeconds, part.StartSeconds);
        clipped.EndSeconds = Math.Min(segment.EndSeconds, part.EndSeconds);
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
