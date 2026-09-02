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
    /// <summary>
    /// The <c>tool:</c> field. Taken from the build rather than typed here,
    /// because a hardcoded string is one more thing to forget on a release —
    /// this one had said 1.2 since v1.2.0.
    /// </summary>
    public static string ToolVersion =>
        BuildProfile.Version.Length > 0 ? "VoxMark " + BuildProfile.Version : "VoxMark";

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
    /// One Markdown covering the whole session, for a recording that was
    /// split into several MP3s.
    ///
    /// The per-part files are still written and still carry the section 10
    /// contract each on its own; this one exists because the split is a
    /// property of the <em>audio</em>, not of the meeting. Handing an agent
    /// six files and asking it to stitch them back together is work the
    /// exporter can simply do once, here — the marks, the gaps and the
    /// transcript are already on a single continuous timeline, so the whole
    /// session is exactly the unsplit document plus a list of which file
    /// holds which stretch of audio. The MP3s are deliberately left alone:
    /// concatenating them would mean re-encoding, and the reason the
    /// recording was split is usually that a single file was unwanted.
    /// </summary>
    public static string BuildCombined(RecordingSession session, IReadOnlyList<AudioPart> parts)
    {
        var whole = new AudioPart
        {
            Index = 1,
            FileName = parts.Count > 0 ? parts[0].FileName : session.AudioFileName,
            StartSeconds = 0,
            EndSeconds = session.AudioDurationSeconds,
        };
        return Build(session, whole, parts.Count, parts);
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
    public static string Build(RecordingSession session, AudioPart part, int partCount,
                               IReadOnlyList<AudioPart>? combined = null)
    {
        var sb = new StringBuilder();

        // Three shapes, not two: one file for one MP3 (the plain contract),
        // one file per part, and — when combined is given — one file for
        // every part at once. "split" stays "this document is one part of
        // several", so all the per-part clipping below is skipped for the
        // combined document exactly as it is for an unsplit session.
        var whole = combined is { Count: > 0 };
        var split = partCount > 1 && !whole;

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

        if (whole)
        {
            // audio_file still answers "which audio is this about?", and then
            // hands over to a list rather than naming one of several files and
            // implying the rest do not exist.
            sb.Append("audio_file: ").Append(combined!.Count).Append(" files — see audio_files\n");
            sb.Append("audio_files:\n");
            foreach (var each in combined)
            {
                sb.Append("  - file: ").Append(each.FileName).Append('\n');
                sb.Append("    start: ").Append(Timestamp(each.StartSeconds)).Append('\n');
                sb.Append("    end: ").Append(Timestamp(each.EndSeconds)).Append('\n');
            }
        }
        else
        {
            sb.Append("audio_file: ").Append(part.FileName).Append('\n');
        }

        sb.Append("audio_format: ").Append(session.AudioFormatDescription).Append('\n');

        if (whole)
        {
            sb.Append("audio_parts: ").Append(combined!.Count).Append(" (this file covers all of them)\n");
            sb.Append("timebase: offset from the start of part 1, continuing across every file\n");
        }
        else if (split)
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
            sb.Append("    title: ").Append(Yaml(speaker.Name)).Append('\n');
            sb.Append("    subtitle: ").Append(Yaml(speaker.Role)).Append('\n');
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
        if (whole) sb.Append(" — complete session");
        sb.Append("\n\n");

        // The brief comes first, before the data it is about. It used to sit
        // last with a pointer under the title, on the reasoning that the
        // section 10 contract should open the file — but a pointer is only as
        // good as the reader that follows it, and an agent handed this file
        // acts on what it reads first. The contract is unchanged, only later
        // in the document.
        AppendAgentInstructions(sb, part, partCount, segments.Count > 0, whole);

        sb.Append("## Speaker segments\n\n");
        sb.Append("Speaker segments in chronological order. Each row is one continuous\n");
        sb.Append("turn marked by the operator during the meeting.\n");
        if (split)
        {
            sb.Append("\nTimes are offsets from the start of part 1, not from the start of\n");
            sb.Append("this file. This file begins at ").Append(Timestamp(part.StartSeconds))
              .Append(" — subtract that to seek within it.\n");
        }
        if (whole)
        {
            sb.Append("\nThe audio was written as ").Append(combined!.Count)
              .Append(" MP3 files, but this document covers the whole\n");
            sb.Append("meeting: every mark, gap and transcript line is here, on one continuous\n");
            sb.Append("timeline that starts at the beginning of the first file. Use `audio_files`\n");
            sb.Append("above to find which file holds a given time, and subtract that file's\n");
            sb.Append("`start` to seek inside it.\n");
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
        if (whole)
        {
            sb.Append("- This session was recorded as ").Append(combined!.Count)
              .Append(" MP3 files. This document covers all of them;\n");
            sb.Append("  each file also has its own Markdown beside it, holding only its own\n");
            sb.Append("  stretch of the meeting.\n");
            sb.Append("- All times are offsets from the start of the first file and run\n");
            sb.Append("  continuously across the set — they do not restart per file.\n");
            sb.Append("- The audio itself was not joined. A turn that ran across a file boundary\n");
            sb.Append("  is one unbroken row here, and its speech is split across two MP3s.\n");
        }
        else if (split)
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

        // The mark-start offset is deliberately *not* noted here. The guide's
        // own sample output carries the line, but it describes how the marks
        // were made rather than what they say, and every reader of this file
        // has already been told to treat the table as the operator's record.
        // The raw press time is still journalled per mark, so the offset
        // remains recoverable without stating it in a file meant for an LLM.
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
    /// It goes <b>first</b>, straight after the title. It used to sit last
    /// with a one-line pointer under the title, which kept the section 10
    /// contract at the top of the file — but a pointer only works on a reader
    /// that follows it, and an agent handed this document acts on what it
    /// reads first. The contract itself is unchanged; it simply starts a few
    /// dozen lines further down, under its own "## Speaker segments" heading.
    ///
    /// Written in English regardless of the meeting's own language — it
    /// addresses the agent, not the room. The report it asks for follows the
    /// meeting instead: verbatim in whatever was spoken, summarised in Korean
    /// for a Korean transcript and in English plus a Korean section otherwise.
    /// </summary>
    private static void AppendAgentInstructions(StringBuilder sb, AudioPart part, int partCount,
                                                bool hasTranscript, bool whole)
    {
        var split = partCount > 1 && !whole;

        sb.Append("## Agent Instructions\n\n");
        sb.Append("You are an AI agent, and this section is the task this file exists for —\n");
        sb.Append("read it before the data below it.\n\n");
        sb.Append("This file records **who held the floor and when**. Your job is to pair that\n");
        sb.Append("with **what was said** and write one new file, `transcript.md`, beside this\n");
        sb.Append("one.\n\n");

        sb.Append("### Step 1 — pick your route\n\n");
        sb.Append("Check what you were actually given, then follow the matching route. Do not\n");
        sb.Append("ask for a file you were not handed.\n\n");

        sb.Append("**Route A — you have the audio");
        if (hasTranscript) sb.Append(" (preferred)");
        sb.Append(".**\n");
        sb.Append(split || whole
            ? "The session's " + partCount + " MP3 files are present.\n"
            : "The MP3 named in `audio_file` is present.\n");
        sb.Append("Transcribe the audio yourself and align it to the speaker rows in\n");
        sb.Append("\"## Speaker segments\" below, per Step 2.\n");
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
            sb.Append("recording. Keep it in the language it was spoken in.\n\n");
        }
        else
        {
            sb.Append("**Route B — you do not have the audio.** This file has no `## Transcript`\n");
            sb.Append("section either, so nothing here records what was said: the speaker table\n");
            sb.Append("below is timings only. Say that plainly and stop. Do not write a\n");
            sb.Append("`transcript.md` from the speaker names and timings alone — a transcript\n");
            sb.Append("of a meeting you cannot hear would be invention, not a transcript.\n\n");
        }

        // Once, and above Step 2 rather than below it: Route B is told to skip
        // Step 2 entirely, so a caution parked underneath it reaches only half
        // the readers. It used to be appended in both places, which printed it
        // twice on every file that carried a transcript.
        AppendRecognitionCautions(sb);

        sb.Append("### Step 2 — align the speech to the speakers (Route A only)\n\n");
        sb.Append("1. Transcribe the audio **in the language it was spoken in** — do not\n");
        sb.Append("   translate as you go. Say at the top of `transcript.md` which language\n");
        sb.Append("   that was.\n");
        sb.Append("2. Align what you hear to the speaker rows below **by time**. A row's\n");
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
        else if (whole)
        {
            sb.Append("The audio came as ").Append(partCount)
              .Append(" MP3 files, but this document already covers the whole\n");
            sb.Append("session. Write one `transcript.md` from it — do not produce one per\n");
            sb.Append("audio file, and do not go looking for the per-part Markdown files: they\n");
            sb.Append("hold the same marks, cut up.\n\n");
        }
        sb.Append("Write every timestamp in the same `HH:MM:SS.mmm` form and on the same\n");
        sb.Append("timebase as this file, so the two can be read side by side.\n\n");

        sb.Append("**Which language to write in.** If the transcript you are working from is\n");
        sb.Append("in Korean, write sections 2 and 3 in Korean and **skip section 4** — it\n");
        sb.Append("would only repeat them. Otherwise write 2 and 3 in English and add section\n");
        sb.Append("4. Section 1 is verbatim either way, in whatever language was spoken.\n\n");

        sb.Append("Then write these sections, in this order:\n\n");
        sb.Append("1. `## Full Transcript` — the verbatim transcript in chronological order,\n");
        sb.Append("   in the language it was spoken in, one block per speaker turn, each\n");
        sb.Append("   headed with the same mark number, speaker id, title and time range this\n");
        sb.Append("   file uses. This is the record of what was actually said: do not\n");
        sb.Append("   summarise, condense or translate it. Removing pure filler (\"um\", false\n");
        sb.Append("   starts) is the only editing allowed.\n");
        sb.Append("2. `## Executive Summary` — the whole meeting in a few paragraphs: what it\n");
        sb.Append("   was about, what was decided, what is still open, and every owner and\n");
        sb.Append("   deadline that was named.\n");
        sb.Append("3. `## Key Points by Speaker` — one subsection per speaker, giving that\n");
        sb.Append("   person's main points, positions, commitments and open questions.\n");
        sb.Append("4. `## 한국어 리포트` — **only when 2 and 3 are in English.** Sections 2\n");
        sb.Append("   and 3 again in Korean, as `### 전체 회의 요약` and\n");
        sb.Append("   `### 화자별 핵심 내용`. Translate the meaning rather than word for\n");
        sb.Append("   word, and leave names, product names and figures as they were spoken.\n\n");

        sb.Append("Ground every statement in your source. Where it is unclear, write\n");
        sb.Append("`[inaudible HH:MM:SS.mmm]` instead of guessing, and never add a decision,\n");
        sb.Append("number, owner or deadline that nobody said.\n\n");
    }

    /// <summary>
    /// What machine recognition gets wrong in this room, and what to do about
    /// it. Emitted <b>once</b>, above Step 2 rather than inside it: Route B is
    /// told to skip Step 2 entirely, so a caution parked underneath it reaches
    /// only half the readers. It was briefly appended in both places, which
    /// printed the whole block twice on every file carrying a transcript.
    ///
    /// Both are reports from the operator rather than general caveats about
    /// whisper, which is why they are stated as facts about this recording
    /// and not as hedging. The abbreviation rule in particular is a
    /// suppression rule, not a "be careful" — an expansion that fits the
    /// letters but not the discussion reads as confident and specific, which
    /// is exactly what makes it dangerous in a summary someone acts on.
    /// </summary>
    private static void AppendRecognitionCautions(StringBuilder sb)
    {
        sb.Append("### What the recogniser gets wrong here\n\n");

        sb.Append("**Occasional words are simply wrong, and not wrong in a plausible way.**\n");
        sb.Append("English recognition on this material now and then returns a word with no\n");
        sb.Append("relation to the sentence around it — not a near-miss, something from a\n");
        sb.Append("different subject entirely. Treat a word that breaks the sense of its own\n");
        sb.Append("sentence as a recognition error rather than as something a person said. In\n");
        sb.Append("the verbatim transcript keep it and mark it `[?]`; do not build any summary\n");
        sb.Append("point, decision or action item on it.\n\n");

        sb.Append("**Industry abbreviations are frequent — expand only the ones the context\n");
        sb.Append("supports.** These meetings use a lot of short forms, and most have several\n");
        sb.Append("possible expansions. Expand an abbreviation only where the surrounding\n");
        sb.Append("discussion makes one reading clearly right, and write it as\n");
        sb.Append("`ABC (expansion)` the first time so the reader can check you.\n\n");

        sb.Append("Where no expansion fits the context, **leave the abbreviation exactly as\n");
        sb.Append("recognised and keep it out of the summary and the key points entirely.**\n");
        sb.Append("It still belongs in the verbatim transcript — that is the record of what\n");
        sb.Append("was said. But a guessed expansion reads as confident and specific, which\n");
        sb.Append("is precisely what makes a wrong one dangerous in a document someone acts\n");
        sb.Append("on. Silence about a term you could not place is the correct outcome, and\n");
        sb.Append("a short list of the abbreviations you left unresolved is more useful than\n");
        sb.Append("any guess at them.\n\n");
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
